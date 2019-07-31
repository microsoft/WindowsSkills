// Copyright (C) Microsoft Corporation. All rights reserved.

using FrameSourceHelper_UWP;
using Microsoft.AI.Skills.SkillInterfacePreview;
using Microsoft.AI.Skills.Vision.ObjectDetectorPreview;
using ObjectDetectorSkillSample;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;


namespace GalleryApp
{
    /// <summary>
    /// Object Detector Skill Page
    /// </summary>
    public sealed partial class ObjectDetectorPage : SkillPageBase, ISkillViewPage
    {
        private IFrameSource m_frameSource = null;

        // Vision Skills
        private ObjectDetectorDescriptor m_descriptor = null;
        private ObjectDetectorBinding m_binding = null;
        private ObjectDetectorSkill m_skill = null;
        private IReadOnlyList<ISkillExecutionDevice> m_availableExecutionDevices = null;

        // Misc
        private BoundingBoxRenderer m_bboxRenderer = null;
        private HashSet<ObjectKind> m_objectKinds = null;

        // Frames
        private SoftwareBitmapSource m_processedBitmapSource;
        private VideoFrame m_renderTargetFrame = null;

        // Performance metrics
        private Stopwatch m_evalStopwatch = new Stopwatch();
        private float m_bindTime = 0;
        private float m_evalTime = 0;

        // Locks
        private SemaphoreSlim m_lock = new SemaphoreSlim(1);

        // Object kind filters related
        private int m_allObjectKindFiltersCount = 0;
        private bool m_IsTriStateCheckBoxClick = false;

        public ObjectDetectorPage()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Create a skill descriptor object to display skill information on UI thumbnail
        /// </summary>
        /// <returns></returns>
        ISkillDescriptor ISkillViewPage.GetSkillDescriptor()
        {
            return new ObjectDetectorDescriptor();
        }

        /// <summary>
        /// Called when page is loaded
        /// Initialize app assets such as skills
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // Disable buttons while we initialize
            await UpdateMediaSourceButtonsAsync(false);

            // Initialize helper class used to render the skill results on screen
            m_bboxRenderer = new BoundingBoxRenderer(UIOverlayCanvas);

            m_lock.Wait();
            {
                NotifyUser("Initializing skill...");
                await UpdateIndicator(Indicator.initialization, ExecutionState.start);
                m_descriptor = new ObjectDetectorDescriptor();
                m_availableExecutionDevices = await m_descriptor.GetSupportedExecutionDevicesAsync();

                await InitializeObjectDetectorAsync();
                await UpdateSkillUIAsync();
            }
            m_lock.Release();

            // Ready to begin, enable buttons
            NotifyUser("Skill initialized. Select a media source from the top to begin.");
            await UpdateMediaSourceButtonsAsync(true);
            await UpdateIndicator(Indicator.initialization, ExecutionState.end);
        }

        /// <summary>
        /// Populate UI with skill information and options
        /// </summary>
        /// <returns></returns>
        private async Task UpdateSkillUIAsync()
        {
            if (Dispatcher.HasThreadAccess)
            {
                // Show skill description members in UI
                UISkillName.Text = m_descriptor.Name;

                UISkillDescription.Text = SkillHelper.SkillHelperMethods.GetSkillDescriptorString(m_descriptor);

                int featureIndex = 0;
                foreach (var featureDesc in m_descriptor.InputFeatureDescriptors)
                {
                    UISkillInputDescription.Text += SkillHelper.SkillHelperMethods.GetSkillFeatureDescriptorString(featureDesc);
                    if (featureIndex++ > 0 && featureIndex < m_descriptor.InputFeatureDescriptors.Count - 1)
                    {
                        UISkillInputDescription.Text += "\n----\n";
                    }
                }

                featureIndex = 0;
                foreach (var featureDesc in m_descriptor.OutputFeatureDescriptors)
                {
                    UISkillOutputDescription.Text += SkillHelper.SkillHelperMethods.GetSkillFeatureDescriptorString(featureDesc);
                    if (featureIndex++ > 0 && featureIndex < m_descriptor.OutputFeatureDescriptors.Count - 1)
                    {
                        UISkillOutputDescription.Text += "\n----\n";
                    }
                }

                if (m_availableExecutionDevices.Count == 0)
                {
                    NotifyUser("No execution devices available, this skill cannot run on this device");
                }
                else
                {
                    // Display available execution devices
                    UISkillExecutionDevices.ItemsSource = m_availableExecutionDevices.Select((device) => $"{device.ExecutionDeviceKind} | {device.Name}");

                    // Set SelectedIndex to index of currently selected device
                    for (int i = 0; i < m_availableExecutionDevices.Count; i++)
                    {
                        if (m_availableExecutionDevices[i].ExecutionDeviceKind == m_binding.Device.ExecutionDeviceKind
                            && m_availableExecutionDevices[i].Name == m_binding.Device.Name)
                        {
                            UISkillExecutionDevices.SelectedIndex = i;
                            break;
                        }
                    }
                }

                // Populate ObjectKind filters list with all possible classes supported by the detector
                // Exclude Undefined label (not used by the detector) from selector list
                UIObjectKindFilters.ItemsSource = Enum.GetValues(typeof(ObjectKind)).Cast<ObjectKind>().Where(kind => kind != ObjectKind.Undefined);
                m_allObjectKindFiltersCount = UIObjectKindFilters.Items.Count;
                UIObjectKindFilters.SelectAll();
            }
            else
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => await UpdateSkillUIAsync());
            }
        }

        /// <summary>
        /// Initialize the ObjectDetector skill
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        private async Task InitializeObjectDetectorAsync(ISkillExecutionDevice device = null)
        {
            if (device != null)
            {
                m_skill = await m_descriptor.CreateSkillAsync(device) as ObjectDetectorSkill;
            }
            else
            {
                m_skill = await m_descriptor.CreateSkillAsync() as ObjectDetectorSkill;
            }
            m_binding = await m_skill.CreateSkillBindingAsync() as ObjectDetectorBinding;

            m_inputImageFeatureDescriptor = m_binding["InputImage"].Descriptor as SkillFeatureImageDescriptor;
        }

        /// <summary>
        /// Bind and evaluate the frame with the ObjectDetector skill
        /// </summary>
        /// <param name="frame"></param>
        /// <returns></returns>
        private async Task DetectObjectsAsync(VideoFrame frame)
        {
            await UpdateIndicator(Indicator.binding, ExecutionState.start);
            m_evalStopwatch.Restart();

            // Bind
            NotifyUser("Binding input image...");
            await m_binding.SetInputImageAsync(frame);

            m_bindTime = (float)m_evalStopwatch.ElapsedTicks / Stopwatch.Frequency * 1000f;
            await UpdateIndicator(Indicator.binding, ExecutionState.end);

            m_evalStopwatch.Restart();

            // Evaluate
            NotifyUser("Running skill over your binding object...");
            await UpdateIndicator(Indicator.evaluating, ExecutionState.start);
            await m_skill.EvaluateAsync(m_binding);

            m_evalTime = (float)m_evalStopwatch.ElapsedTicks / Stopwatch.Frequency * 1000f;
            m_evalStopwatch.Stop();
            await UpdateIndicator(Indicator.evaluating, ExecutionState.end);
        }

        /// <summary>
        /// Render ObjectDetector skill results
        /// </summary>
        /// <param name="frame"></param>
        /// <param name="objectDetections"></param>
        /// <returns></returns>
        private async Task DisplayFrameAndResultAsync(VideoFrame frame)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    SoftwareBitmap targetSoftwareBitmap = frame.SoftwareBitmap;

                    // If we receive a Direct3DSurface-backed VideoFrame, convert to a SoftwareBitmap in a format that can be rendered via the UI element
                    if (targetSoftwareBitmap == null)
                    {
                        if (m_renderTargetFrame == null)
                        {
                            m_renderTargetFrame = new VideoFrame(BitmapPixelFormat.Bgra8, frame.Direct3DSurface.Description.Width, frame.Direct3DSurface.Description.Height, BitmapAlphaMode.Ignore);
                        }

                        // Leverage the VideoFrame.CopyToAsync() method that can convert the input Direct3DSurface-backed VideoFrame to a SoftwareBitmap-backed VideoFrame
                        await frame.CopyToAsync(m_renderTargetFrame);
                        targetSoftwareBitmap = m_renderTargetFrame.SoftwareBitmap;
                    }
                    // Else, if we receive a SoftwareBitmap-backed VideoFrame, if its format cannot already be rendered via the UI element, convert it accordingly
                    else
                    {
                        if (targetSoftwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || targetSoftwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                        {
                            if (m_renderTargetFrame == null)
                            {
                                m_renderTargetFrame = new VideoFrame(BitmapPixelFormat.Bgra8, targetSoftwareBitmap.PixelWidth, targetSoftwareBitmap.PixelHeight, BitmapAlphaMode.Ignore);
                            }

                            // Leverage the VideoFrame.CopyToAsync() method that can convert the input SoftwareBitmap-backed VideoFrame to a different format
                            await frame.CopyToAsync(m_renderTargetFrame);
                            targetSoftwareBitmap = m_renderTargetFrame.SoftwareBitmap;
                        }
                    }
                    await m_processedBitmapSource.SetBitmapAsync(targetSoftwareBitmap);

                    // Retrieve and filter results
                    IReadOnlyList<ObjectDetectorResult> objectDetections = m_binding.DetectedObjects.Where(det => m_objectKinds.Contains(det.Kind)).ToList();

                    NotifyUser("Displaying result...");
                    // Update displayed results
                    m_bboxRenderer.Render(objectDetections);

                    // Update the displayed performance text
                    UIPerfTextBlock.Text = $"bind: {m_bindTime.ToString("F2")}ms, eval: {m_evalTime.ToString("F2")}ms";
                }
                catch (TaskCanceledException)
                {
                    // no-op: we expect this exception when we change media sources
                    // and can safely ignore/continue
                }
                catch (Exception ex)
                {
                    NotifyUser($"Exception while rendering results: {ex.Message}");
                    await UpdateIndicator(Indicator.done, ExecutionState.error);
                }
            });
        }

        /// <summary>
        /// Configure an IFrameSource from a StorageFile or MediaCapture instance to produce optionally a specified format of frame
        /// </summary>
        /// <param name="source"></param>
        /// <param name="inputImageDescriptor"></param>
        /// <returns></returns>
        protected override async Task ConfigureFrameSourceAsync(object source, ISkillFeatureImageDescriptor inputImageDescriptor = null)
        {
            await m_lock.WaitAsync();
            {
                // Reset bitmap rendering component
                UIProcessedPreview.Source = null;
                m_renderTargetFrame = null;
                m_processedBitmapSource = new SoftwareBitmapSource();
                UIProcessedPreview.Source = m_processedBitmapSource;

                // Clean up previous frame source
                if (m_frameSource != null)
                {
                    m_frameSource.FrameArrived -= FrameSource_FrameAvailable;
                    var disposableFrameSource = m_frameSource as IDisposable;
                    if (disposableFrameSource != null)
                    {
                        // Lock disposal based on frame source consumers
                        disposableFrameSource.Dispose();
                    }
                }

                // Create new frame source and register a callback if the source fails along the way
                m_frameSource = await FrameSourceFactory.CreateFrameSourceAsync(
                    source,
                    (sender, message) =>
                    {
                        NotifyUser(message);
                    },
                    inputImageDescriptor);

                // TODO: Workaround for a bug in ObjectDetectorBinding when binding consecutively VideoFrames with Direct3DSurface and SoftwareBitmap
                m_binding = await m_skill.CreateSkillBindingAsync() as ObjectDetectorBinding;
            }
            m_lock.Release();

            // If we obtained a valid frame source, start it
            if (m_frameSource != null)
            {
                m_frameSource.FrameArrived += FrameSource_FrameAvailable;
                await m_frameSource.StartAsync();
            }
        }

        /// <summary>
        /// FrameAvailable event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="frame"></param>
        private void FrameSource_FrameAvailable(object sender, VideoFrame frame)
        {
            // Locking behavior, so only one skill execution happens at a time
            if (m_lock.Wait(0))
            {
#pragma warning disable CS4014
                // Purposely don't await this: want handler to exit ASAP
                // so that real time capture doesn't wait for completion.
                // Instead, we unlock only when processing finishes ensuring that
                // only one execution is active at a time, dropping frames or
                // aborting skill runs as necessary
                Task.Run(async () =>
                {
                    try
                    {
                        await UpdateIndicator(Indicator.binding, ExecutionState.reset);
                        await UpdateIndicator(Indicator.evaluating, ExecutionState.reset);
                        await UpdateIndicator(Indicator.done, ExecutionState.reset);

                        await DetectObjectsAsync(frame);
                        await DisplayFrameAndResultAsync(frame);
                        UpdateIndicator(Indicator.done, ExecutionState.end);
                    }
                    catch (Exception ex)
                    {
                        NotifyUser(ex.Message);
                        UpdateIndicator(Indicator.done, ExecutionState.error);
                    }
                    finally
                    {
                        m_lock.Release();
                    }
                });
#pragma warning restore CS4014
            }
        }

        /// <summary>
        /// Triggers when a skill execution device is selected from the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void UISkillExecutionDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedDevice = m_availableExecutionDevices[UISkillExecutionDevices.SelectedIndex];
            await m_lock.WaitAsync();
            {
                await InitializeObjectDetectorAsync(selectedDevice);
            }
            m_lock.Release();
            FrameSourceStartAsync();
        }

        /// <summary>
        /// Triggers when the object kind filter list is changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void UIObjectKindFilters_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await m_lock.WaitAsync();
            {
                m_objectKinds = UIObjectKindFilters.SelectedItems.Cast<ObjectKind>().ToHashSet();
            }
            m_lock.Release();

            // Update the tri-state checkbox if the filter update are not trigger by the tri-state checkbox
            if (!m_IsTriStateCheckBoxClick)
            {
                if (m_objectKinds.Count == m_allObjectKindFiltersCount)
                {
                    UITriStateCheckBox.IsChecked = true;
                }
                else if (m_objectKinds.Count > 0)
                {
                    UITriStateCheckBox.IsChecked = null;
                }
                else
                {
                    UITriStateCheckBox.IsChecked = false;
                }
            }

            m_IsTriStateCheckBoxClick = false;

            FrameSourceStartAsync();
        }

        /// <summary>
        /// Triggers when the image control is resized, making sure the canvas size stays in sync with the frame display control.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void UIProcessedPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Make sure the aspect ratio is honored when rendering the body limbs
            float cameraAspectRatio = (float)m_frameSource.FrameWidth / m_frameSource.FrameHeight;
            float previewAspectRatio = (float)(UIProcessedPreview.ActualWidth / UIProcessedPreview.ActualHeight);
            UIOverlayCanvas.Width = cameraAspectRatio >= previewAspectRatio ? UIProcessedPreview.ActualWidth : UIProcessedPreview.ActualHeight * cameraAspectRatio;
            UIOverlayCanvas.Height = cameraAspectRatio >= previewAspectRatio ? UIProcessedPreview.ActualWidth / cameraAspectRatio : UIProcessedPreview.ActualHeight;

            m_bboxRenderer.ResizeContent(e);
        }

        /// <summary>
        /// Triggers when Tri
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TriStateCheckBox_Click(object sender, RoutedEventArgs e)
        {
            m_IsTriStateCheckBoxClick = true;

            if (UITriStateCheckBox.IsChecked == true)
            {
                UIObjectKindFilters.SelectAll();
            }
            else if (UITriStateCheckBox.IsChecked == false)
            {
                UIObjectKindFilters.SelectedIndex = -1;
            }
        }

        /// <summary>
        /// If valid frame source is obtained, run skill
        /// </summary>
        private async void FrameSourceStartAsync()
        {
            if (m_frameSource != null)
            {
                await m_frameSource.StartAsync();
            }
        }

    }
}