﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.IO;
using System.IO.IsolatedStorage;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Shapes;
using Microsoft.Phone;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Info;
using Microsoft.Phone.Tasks;
using Microsoft.Phone.Shell;
using Microsoft.Xna.Framework.Media;

namespace MagicPhotos
{
    public partial class RecolorPage : PhoneApplicationPage
    {
        private const int MODE_NONE     = 0,
                          MODE_SCROLL   = 1,
                          MODE_ORIGINAL = 2,
                          MODE_EFFECTED = 3,
                          MODE_COLOR    = 4;

        private const int MAX_IMAGE_WIDTH  = 1400,
                          MAX_IMAGE_HEIGHT = 1400;

        private const int HELPER_POINT_WIDTH  = 6,
                          HELPER_POINT_HEIGHT = 6;

        private const int BRUSH_RADIUS = 24,
                          UNDO_DEPTH   = 4;

        private const double REDUCTION_MPIX_LIMIT = 1.0;

        private bool                  loadImageOnLayoutUpdate,
                                      loadImageCancelled,
                                      needImageReduction,
                                      editedImageChanged;
        private int                   selectedMode,
                                      selectedHue;
        private double                currentScale,
                                      initialScale;
        private List<int[]>           undoStack;
        private WriteableBitmap       editedBitmap,
                                      originalBitmap,
                                      helperBitmap,
                                      brushTemplateBitmap,
                                      brushBitmap;
        private PhotoChooserTask      photoChooserTask;
        private MarketplaceDetailTask marketplaceDetailTask;

        public RecolorPage()
        {
            InitializeComponent();

            this.loadImageOnLayoutUpdate = true;
            this.loadImageCancelled      = false;
            this.editedImageChanged      = false;
            this.selectedMode            = MODE_NONE;
            this.selectedHue             = 0;
            this.currentScale            = 1.0;
            this.initialScale            = 1.0;
            this.undoStack               = new List<int[]>();
            this.editedBitmap            = null;
            this.originalBitmap          = null;
            this.helperBitmap            = null;
            this.brushBitmap             = null;

            try
            {
                long limit = (long)DeviceExtendedProperties.GetValue("ApplicationWorkingSetLimit");

                if (limit <= 90L * 1024L * 1024L)
                {
                    this.needImageReduction = true;
                }
                else
                {
                    this.needImageReduction = false;
                }
            }
            catch (Exception)
            {
                this.needImageReduction = false;
            }

            this.brushTemplateBitmap = new WriteableBitmap(BRUSH_RADIUS * 2, BRUSH_RADIUS * 2);

            for (int x = 0; x < this.brushTemplateBitmap.PixelWidth; x++)
            {
                for (int y = 0; y < this.brushTemplateBitmap.PixelHeight; y++)
                {
                    if (Math.Sqrt(Math.Pow(x - BRUSH_RADIUS, 2) + Math.Pow(y - BRUSH_RADIUS, 2)) <= BRUSH_RADIUS)
                    {
                        this.brushTemplateBitmap.SetPixel(x, y, (0xFF << 24) | 0xFFFFFF);
                    }
                    else
                    {
                        this.brushTemplateBitmap.SetPixel(x, y, (0x00 << 24) | 0xFFFFFF);
                    }
                }
            }

            this.photoChooserTask            = new PhotoChooserTask();
            this.photoChooserTask.ShowCamera = true;
            this.photoChooserTask.Completed += new EventHandler<PhotoResult>(photoChooserTask_Completed);

            this.marketplaceDetailTask                   = new MarketplaceDetailTask();
#if DEBUG_TRIAL
            this.marketplaceDetailTask.ContentType       = MarketplaceContentType.Applications;
            this.marketplaceDetailTask.ContentIdentifier = "ae587193-24c3-49ff-8743-88f5f05907c1";
#endif

            ApplicationBarIconButton button;

            button        = new ApplicationBarIconButton(new Uri("/Images/save.png", UriKind.Relative));
            button.Text   = AppResources.ApplicationBarButtonSaveText;
            button.Click += new EventHandler(SaveButton_Click);

            this.ApplicationBar.Buttons.Add(button);

            button        = new ApplicationBarIconButton(new Uri("/Images/help.png", UriKind.Relative));
            button.Text   = AppResources.ApplicationBarButtonHelpText;
            button.Click += new EventHandler(HelpButton_Click);

            this.ApplicationBar.Buttons.Add(button);

            if ((System.Windows.Visibility)App.Current.Resources["PhoneDarkThemeVisibility"] == System.Windows.Visibility.Visible)
            {
                this.UndoButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri("/Images/dark/undo.png", UriKind.Relative)) };
            }
            else
            {
                this.UndoButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri("/Images/light/undo.png", UriKind.Relative)) };
            }

            UpdateModeButtons();
        }

        protected override void OnNavigatedTo(System.Windows.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (this.loadImageCancelled)
            {
                if (NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }

                this.loadImageCancelled = false;
            }
        }

        private double Normalize(double d)
        {
            if (d < 0) d += 1;
            if (d > 1) d -= 1;
            return d;
        }

        private double GetComponent(double tc, double p, double q)
        {
            if (tc < (1.0 / 6.0))
            {
                return p + ((q - p) * 6 * tc);
            }
            if (tc < .5)
            {
                return q;
            }
            if (tc < (2.0 / 3.0))
            {
                return p + ((q - p) * 6 * ((2.0 / 3.0) - tc));
            }
            return p;
        }

        private void UpdateModeButtons()
        {
            string img_dir;

            if ((System.Windows.Visibility)App.Current.Resources["PhoneDarkThemeVisibility"] == System.Windows.Visibility.Visible)
            {
                img_dir = "/Images/dark";
            }
            else
            {
                img_dir = "/Images/light";
            }

            if (this.selectedMode == MODE_SCROLL)
            {
                this.ScrollModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_scroll_selected.png", UriKind.Relative)) };
            }
            else
            {
                this.ScrollModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_scroll.png", UriKind.Relative)) };
            }

            if (this.selectedMode == MODE_ORIGINAL)
            {
                this.OriginalModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_original_selected.png", UriKind.Relative)) };
            }
            else
            {
                this.OriginalModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_original.png", UriKind.Relative)) };
            }

            if (this.selectedMode == MODE_EFFECTED)
            {
                this.EffectedModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_effected_selected.png", UriKind.Relative)) };
            }
            else
            {
                this.EffectedModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_effected.png", UriKind.Relative)) };
            }

            if (this.selectedMode == MODE_COLOR)
            {
                this.ColorModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_color_selected.png", UriKind.Relative)) };
            }
            else
            {
                this.ColorModeButton.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(img_dir + "/mode_color.png", UriKind.Relative)) };
            }
        }

        private void MoveHelper(Point touch_point)
        {
            if (touch_point.Y < this.HelperBorder.Height * 1.5)
            {
                if (touch_point.X < this.HelperBorder.Width * 1.5)
                {
                    this.HelperBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                }
                else if (touch_point.X > this.EditorGrid.ActualWidth - this.HelperBorder.Width * 1.5)
                {
                    this.HelperBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                }
            }
        }

        private void UpdateHelper(bool visible, Point touch_point)
        {
            if (visible)
            {
                int width  = (int)(this.HelperImage.Width  / this.currentScale) < this.editedBitmap.PixelWidth  ? (int)(this.HelperImage.Width  / this.currentScale) : this.editedBitmap.PixelWidth;
                int height = (int)(this.HelperImage.Height / this.currentScale) < this.editedBitmap.PixelHeight ? (int)(this.HelperImage.Height / this.currentScale) : this.editedBitmap.PixelHeight;

                int x = (x = (int)(touch_point.X / this.currentScale) - width  / 2 < 0 ? 0 : (int)(touch_point.X / this.currentScale) - width  / 2) > this.editedBitmap.PixelWidth  - width  ? this.editedBitmap.PixelWidth  - width  : x;
                int y = (y = (int)(touch_point.Y / this.currentScale) - height / 2 < 0 ? 0 : (int)(touch_point.Y / this.currentScale) - height / 2) > this.editedBitmap.PixelHeight - height ? this.editedBitmap.PixelHeight - height : y;

                this.helperBitmap = this.editedBitmap.Crop(x, y, width, height);

                int touch_x1 = (int)(touch_point.X / this.currentScale) - x - HELPER_POINT_WIDTH  / 2;
                int touch_y1 = (int)(touch_point.Y / this.currentScale) - y - HELPER_POINT_HEIGHT / 2;
                int touch_x2 = (int)(touch_point.X / this.currentScale) - x + HELPER_POINT_WIDTH  / 2;
                int touch_y2 = (int)(touch_point.Y / this.currentScale) - y + HELPER_POINT_HEIGHT / 2;

                if (touch_x1 > 0 && touch_x1 < this.helperBitmap.PixelWidth && touch_y1 > 0 && touch_y1 < this.helperBitmap.PixelHeight &&
                    touch_x2 > 0 && touch_x2 < this.helperBitmap.PixelWidth && touch_y2 > 0 && touch_y2 < this.helperBitmap.PixelHeight)
                {
                    this.helperBitmap.DrawRectangle(touch_x1, touch_y1, touch_x2, touch_y2, 0x00FFFFFF);
                }

                this.HelperImage.Source = this.helperBitmap;

                this.HelperBorder.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                this.HelperBorder.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void UpdateColorBorder()
        {
            if (this.selectedMode == MODE_COLOR)
            {
                this.ColorBorder.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                this.ColorBorder.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void LoadImage(WriteableBitmap bitmap)
        {
            if (this.needImageReduction && bitmap.PixelWidth * bitmap.PixelHeight > REDUCTION_MPIX_LIMIT * 1000000.0)
            {
                double factor = Math.Sqrt((bitmap.PixelWidth * bitmap.PixelHeight) / (REDUCTION_MPIX_LIMIT * 1000000.0));

                bitmap = bitmap.Resize((int)(bitmap.PixelWidth / factor), (int)(bitmap.PixelHeight / factor), WriteableBitmapExtensions.Interpolation.NearestNeighbor);
            }

            if (bitmap.PixelWidth != 0 && bitmap.PixelHeight != 0)
            {
                this.editedImageChanged = true;
                this.selectedMode       = MODE_SCROLL;

                this.undoStack.Clear();

                this.originalBitmap = bitmap.Clone();
                this.editedBitmap   = bitmap.Clone();

                if (this.editedBitmap.PixelWidth > this.editedBitmap.PixelHeight)
                {
                    this.currentScale = this.EditorScrollViewer.ViewportWidth / this.editedBitmap.PixelWidth;
                }
                else
                {
                    this.currentScale = this.EditorScrollViewer.ViewportHeight / this.editedBitmap.PixelHeight;
                }

                this.EditorImage.Visibility = System.Windows.Visibility.Visible;
                this.EditorImage.Source     = this.editedBitmap;
                this.EditorImage.Width      = this.editedBitmap.PixelWidth  * this.currentScale;
                this.EditorImage.Height     = this.editedBitmap.PixelHeight * this.currentScale;

                int brush_width  = (int)(BRUSH_RADIUS / this.currentScale) * 2 < this.editedBitmap.PixelWidth  ? (int)(BRUSH_RADIUS / this.currentScale) * 2 : this.editedBitmap.PixelWidth;
                int brush_height = (int)(BRUSH_RADIUS / this.currentScale) * 2 < this.editedBitmap.PixelHeight ? (int)(BRUSH_RADIUS / this.currentScale) * 2 : this.editedBitmap.PixelHeight;

                this.brushBitmap = this.brushTemplateBitmap.Resize(brush_width, brush_height, WriteableBitmapExtensions.Interpolation.NearestNeighbor);

                UpdateModeButtons();
            }
        }

        private void SaveUndoImage()
        {
            if (this.editedBitmap.Pixels.Length > 0)
            {
                int[] pixels = new int[this.editedBitmap.Pixels.Length];

                this.editedBitmap.Pixels.CopyTo(pixels, 0);

                this.undoStack.Add(pixels);

                if (this.undoStack.Count > UNDO_DEPTH)
                {
                    for (int i = 0; i < this.undoStack.Count - UNDO_DEPTH; i++)
                    {
                        this.undoStack.RemoveAt(0);
                    }
                }
            }
        }

        private void ChangeBitmap(Point touch_point)
        {
            int   radius  = (int)(BRUSH_RADIUS / this.currentScale);
            Point t_point = new Point(touch_point.X / this.currentScale, touch_point.Y / this.currentScale);

            if (this.selectedMode == MODE_ORIGINAL || this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
            {
                int width  = this.brushBitmap.PixelWidth;
                int height = this.brushBitmap.PixelHeight;

                Rect rect = new Rect();

                rect.X      = (rect.X = t_point.X - width  / 2 < 0 ? 0 : t_point.X - width  / 2) > this.editedBitmap.PixelWidth  - width  ? this.editedBitmap.PixelWidth  - width  : rect.X;
                rect.Y      = (rect.Y = t_point.Y - height / 2 < 0 ? 0 : t_point.Y - height / 2) > this.editedBitmap.PixelHeight - height ? this.editedBitmap.PixelHeight - height : rect.Y;
                rect.Width  = width;
                rect.Height = height;

                Rect brh = new Rect(0, 0, width, height);

                WriteableBitmap brush_bitmap = this.brushBitmap.Clone();

                brush_bitmap.Blit(brh, this.originalBitmap, rect, WriteableBitmapExtensions.BlendMode.Multiply);

                if (this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
                {
                    brush_bitmap.ForEach((x, y, color) => {
                        double r = (double)color.R / 255.0;
                        double g = (double)color.G / 255.0;
                        double b = (double)color.B / 255.0;

                        double max = Math.Max(b, Math.Max(r, g));
                        double min = Math.Min(b, Math.Min(r, g));

                        double s = 0;
                        double l = 0.5 * (max + min);

                        if (max == min)
                        {
                            s = 0;
                        }
                        else if (l <= 0.5)
                        {
                            s = (max - min) / (2 * l);
                        }
                        else if (l > 0.5)
                        {
                            s = (max - min) / (2 - 2 * l);
                        }

                        double q = 0;

                        if (l < 0.5)
                        {
                            q = l * (1 + s);
                        }
                        else
                        {
                            q = l + s - (l * s);
                        }

                        double p  = (2 * l) - q;
                        double hk = (double)this.selectedHue / 360.0;

                        r = GetComponent(Normalize(hk + (1.0 / 3.0)), p, q) * 255.0 + 0.5;
                        g = GetComponent(Normalize(hk),               p, q) * 255.0 + 0.5;
                        b = GetComponent(Normalize(hk - (1.0 / 3.0)), p, q) * 255.0 + 0.5;

                        byte rgb_r = (byte)((r > 255 ? 255 : r) < 0 ? 0 : r);
                        byte rgb_g = (byte)((g > 255 ? 255 : g) < 0 ? 0 : g);
                        byte rgb_b = (byte)((b > 255 ? 255 : b) < 0 ? 0 : b);

                        return Color.FromArgb(color.A, rgb_r, rgb_g, rgb_b);
                    });
                }

                this.editedBitmap.Blit(rect, brush_bitmap, brh, WriteableBitmapExtensions.BlendMode.Alpha);

                this.EditorImage.Source = this.editedBitmap;
            }
        }

        private void RecolorPage_LayoutUpdated(object sender, EventArgs e)
        {
            if (this.loadImageOnLayoutUpdate)
            {
                try
                {
                    using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                    {
                        string file_name = "image.jpg";

                        if (store.FileExists(file_name))
                        {
                            using (IsolatedStorageFileStream stream = store.OpenFile(file_name, FileMode.Open, FileAccess.Read))
                            {
                                WriteableBitmap bitmap = PictureDecoder.DecodeJpeg(stream);

                                LoadImage(bitmap);
                            }

                            store.DeleteFile(file_name);
                        }
                        else
                        {
                            this.photoChooserTask.Show();
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(AppResources.MessageBoxMessageImageOpenError + " " + ex.Message.ToString(), AppResources.MessageBoxHeaderError, MessageBoxButton.OK);
                }

                this.loadImageOnLayoutUpdate = false;
            }
        }

        private void RecolorPage_BackKeyPress(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this.editedImageChanged)
            {
                MessageBoxResult result = MessageBox.Show(AppResources.MessageBoxMessageUnsavedImageQuestion, AppResources.MessageBoxHeaderWarning, MessageBoxButton.OKCancel);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                }
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.undoStack.Count > 0)
            {
                this.editedImageChanged = true;

                int[] pixels = this.undoStack.ElementAt(this.undoStack.Count - 1);

                this.undoStack.RemoveAt(this.undoStack.Count - 1);

                if (pixels.Length == this.editedBitmap.Pixels.Length)
                {
                    pixels.CopyTo(this.editedBitmap.Pixels, 0);
                }

                this.EditorImage.Source = this.editedBitmap;
            }
        }

        private void ScrollModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.selectedMode != MODE_NONE)
            {
                this.selectedMode = MODE_SCROLL;

                UpdateModeButtons();
                UpdateColorBorder();
            }
        }

        private void OriginalModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.selectedMode != MODE_NONE)
            {
                this.selectedMode = MODE_ORIGINAL;

                UpdateModeButtons();
                UpdateColorBorder();
            }
        }

        private void EffectedModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.selectedMode != MODE_NONE)
            {
                this.selectedMode = MODE_EFFECTED;

                UpdateModeButtons();
                UpdateColorBorder();
            }
        }

        private void ColorModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.selectedMode != MODE_NONE)
            {
                this.selectedMode = MODE_COLOR;

                UpdateModeButtons();
                UpdateColorBorder();
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (this.editedBitmap != null)
            {
                if ((Application.Current as App).TrialMode)
                {
                    MessageBoxResult result = MessageBox.Show(AppResources.MessageBoxMessageTrialVersionQuestion, AppResources.MessageBoxHeaderInfo, MessageBoxButton.OKCancel);

                    if (result == MessageBoxResult.OK)
                    {
                        try
                        {
                            this.marketplaceDetailTask.Show();
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(AppResources.MessageBoxMessageMarketplaceOpenError + " " + ex.Message.ToString(), AppResources.MessageBoxHeaderError, MessageBoxButton.OK);
                        }
                    }
                }
                else
                {
                    try
                    {
                        using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                        {
                            string file_name = "image.jpg";

                            if (store.FileExists(file_name))
                            {
                                store.DeleteFile(file_name);
                            }

                            using (IsolatedStorageFileStream stream = store.CreateFile(file_name))
                            {
                                this.editedBitmap.SaveJpeg(stream, this.editedBitmap.PixelWidth, this.editedBitmap.PixelHeight, 0, 100);
                            }

                            using (IsolatedStorageFileStream stream = store.OpenFile(file_name, FileMode.Open, FileAccess.Read))
                            {
                                using (MediaLibrary library = new MediaLibrary())
                                {
                                    library.SavePicture(file_name, stream);
                                }
                            }

                            store.DeleteFile(file_name);
                        }

                        this.editedImageChanged = false;

                        MessageBox.Show(AppResources.MessageBoxMessageImageSavedInfo, AppResources.MessageBoxHeaderInfo, MessageBoxButton.OK);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(AppResources.MessageBoxMessageImageSaveError + " " + ex.Message.ToString(), AppResources.MessageBoxHeaderError, MessageBoxButton.OK);
                    }
                }
            }
        }

        private void HelpButton_Click(object sender, EventArgs e)
        {
            NavigationService.Navigate(new Uri("/HelpPage.xaml", UriKind.Relative));
        }

        private void photoChooserTask_Completed(object sender, PhotoResult e)
        {
            if (e != null && e.TaskResult == TaskResult.OK && e.ChosenPhoto != null)
            {
                WriteableBitmap bitmap = new WriteableBitmap(0, 0);

                bitmap.SetSource(e.ChosenPhoto);

                LoadImage(bitmap);
            }
            else
            {
                this.loadImageCancelled = true;
            }
        }

        private void EditorGrid_MouseEnter(object sender, MouseEventArgs e)
        {
            MoveHelper(e.GetPosition(this.EditorGrid));
        }

        private void EditorGrid_MouseMove(object sender, MouseEventArgs e)
        {
            MoveHelper(e.GetPosition(this.EditorGrid));
        }

        private void EditorGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            MoveHelper(e.GetPosition(this.EditorGrid));
        }

        private void ColorRectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            double top    = Math.Max(0, Math.Min(this.ColorRectangle.ActualHeight - this.ColorSliderRectangle.ActualHeight, e.GetPosition(this.ColorRectangle).Y));
            double bottom = this.ColorRectangle.ActualHeight - this.ColorSliderRectangle.ActualHeight - top;

            this.ColorSliderRectangle.Margin = new Thickness(0, top, 0, bottom);

            this.selectedHue = (int)(Math.Max(0, Math.Min(this.ColorRectangle.ActualHeight, e.GetPosition(this.ColorRectangle).Y)) * (360 / this.ColorRectangle.ActualHeight));
        }

        private void ColorRectangle_MouseMove(object sender, MouseEventArgs e)
        {
            double top    = Math.Max(0, Math.Min(this.ColorRectangle.ActualHeight - this.ColorSliderRectangle.ActualHeight, e.GetPosition(this.ColorRectangle).Y));
            double bottom = this.ColorRectangle.ActualHeight - this.ColorSliderRectangle.ActualHeight - top;

            this.ColorSliderRectangle.Margin = new Thickness(0, top, 0, bottom);

            this.selectedHue = (int)(Math.Max(0, Math.Min(this.ColorRectangle.ActualHeight, e.GetPosition(this.ColorRectangle).Y)) * (360 / this.ColorRectangle.ActualHeight));
        }

        private void EditorImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.EditorImage.CaptureMouse();

            if (this.selectedMode == MODE_ORIGINAL || this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
            {
                this.editedImageChanged = true;

                SaveUndoImage();

                ChangeBitmap(e.GetPosition(this.EditorImage));

                UpdateHelper(true, e.GetPosition(this.EditorImage));
            }
        }

        private void EditorImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (this.selectedMode == MODE_ORIGINAL || this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
            {
                ChangeBitmap(e.GetPosition(this.EditorImage));

                UpdateHelper(true, e.GetPosition(this.EditorImage));
            }
        }

        private void EditorImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            this.EditorImage.ReleaseMouseCapture();

            if (this.selectedMode == MODE_ORIGINAL || this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
            {
                UpdateHelper(false, e.GetPosition(this.EditorImage));
            }
        }

        private void EditorImage_MouseLeave(object sender, MouseEventArgs e)
        {
            if (this.selectedMode == MODE_ORIGINAL || this.selectedMode == MODE_EFFECTED || this.selectedMode == MODE_COLOR)
            {
                UpdateHelper(false, e.GetPosition(this.EditorImage));
            }
        }

        private void EditorImage_PinchStarted(object sender, PinchStartedGestureEventArgs e)
        {
            if (this.selectedMode == MODE_SCROLL)
            {
                e.Handled = true;

                this.initialScale = this.currentScale;
            }
        }

        private void EditorImage_PinchDelta(object sender, PinchGestureEventArgs e)
        {
            if (this.selectedMode == MODE_SCROLL)
            {
                e.Handled = true;

                double scale  = this.initialScale             * e.DistanceRatio;
                double width  = this.editedBitmap.PixelWidth  * scale;
                double height = this.editedBitmap.PixelHeight * scale;

                if ((width >= this.EditorScrollViewer.ViewportWidth || height >= this.EditorScrollViewer.ViewportHeight) &&
                    (width <= MAX_IMAGE_WIDTH                       && height <= MAX_IMAGE_HEIGHT))
                {
                    this.currentScale       = scale;
                    this.EditorImage.Width  = width;
                    this.EditorImage.Height = height;

                    int brush_width  = (int)(BRUSH_RADIUS / this.currentScale) * 2 < this.editedBitmap.PixelWidth  ? (int)(BRUSH_RADIUS / this.currentScale) * 2 : this.editedBitmap.PixelWidth;
                    int brush_height = (int)(BRUSH_RADIUS / this.currentScale) * 2 < this.editedBitmap.PixelHeight ? (int)(BRUSH_RADIUS / this.currentScale) * 2 : this.editedBitmap.PixelHeight;

                    this.brushBitmap = this.brushTemplateBitmap.Resize(brush_width, brush_height, WriteableBitmapExtensions.Interpolation.NearestNeighbor);
                }
            }
        }

        private void EditorScrollViewer_ManipulationStarted(object sender, ManipulationStartedEventArgs e)
        {
            if (this.selectedMode != MODE_SCROLL)
            {
                e.Handled = true;
                e.Complete();
            }
        }

        private void EditorScrollViewer_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (this.selectedMode != MODE_SCROLL)
            {
                e.Handled = true;
                e.Complete();
            }
        }

        private void EditorScrollViewer_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            if (this.selectedMode != MODE_SCROLL)
            {
                e.Handled = true;
            }
        }
    }
}