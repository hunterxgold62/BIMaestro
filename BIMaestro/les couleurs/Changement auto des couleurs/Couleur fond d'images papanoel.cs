using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Licensing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Transform = System.Windows.Media.Transform;

namespace Couleur
{
    [Transaction(TransactionMode.Manual)]
    public class PapanoelCommand : BaseTrackedCommand
    {
        private static Random _rnd = new Random();
        private static bool _isRunning = false; // Contrôle d'exécution
        private static Task _colorChangeTask;
        private static CancellationTokenSource _cts;
        private static int _clickCount = 0;
        private static readonly int DoubleClickThreshold = 300; // Temps pour le double-clic en ms
        private static DateTime _lastClickTime = DateTime.MinValue;
        protected override string ButtonId => "PapanoelCommand";

        private static readonly Dictionary<Border, Brush> _originalBorderBackground = new Dictionary<Border, Brush>();
        private static readonly Dictionary<Border, Brush> _originalBorderBrush = new Dictionary<Border, Brush>();
        private static readonly Dictionary<Border, Thickness> _originalBorderThickness = new Dictionary<Border, Thickness>();
        private static readonly Dictionary<Panel, Brush> _originalPanelBackground = new Dictionary<Panel, Brush>();
        private static readonly Dictionary<Control, Brush> _originalControlBackground = new Dictionary<Control, Brush>();
        private static readonly Dictionary<Control, Brush> _originalControlForeground = new Dictionary<Control, Brush>();
        private static readonly Dictionary<Control, Brush> _originalControlBorderBrush = new Dictionary<Control, Brush>();
        private static readonly Dictionary<TextBlock, Brush> _originalTextBlockBackground = new Dictionary<TextBlock, Brush>();
        private static readonly Dictionary<TextBlock, Brush> _originalTextBlockForeground = new Dictionary<TextBlock, Brush>();
        private static readonly Dictionary<Shape, Brush> _originalShapeFill = new Dictionary<Shape, Brush>();
        private static readonly Dictionary<Shape, Brush> _originalShapeStroke = new Dictionary<Shape, Brush>();
        private static readonly Dictionary<Shape, DoubleCollection> _originalShapeDashArray = new Dictionary<Shape, DoubleCollection>();
        private static readonly Dictionary<Shape, double> _originalShapeStrokeThickness = new Dictionary<Shape, double>();
        private static readonly Dictionary<FrameworkElement, Transform> _originalRenderTransform = new Dictionary<FrameworkElement, Transform>();
        private static readonly Dictionary<FrameworkElement, Transform> _originalLayoutTransform = new Dictionary<FrameworkElement, Transform>();
        private static readonly Dictionary<FrameworkElement, Point> _originalRenderTransformOrigin = new Dictionary<FrameworkElement, Point>();
        private static readonly Dictionary<FrameworkElement, Effect> _originalEffects = new Dictionary<FrameworkElement, Effect>();
        private static readonly Dictionary<FrameworkElement, double> _originalOpacity = new Dictionary<FrameworkElement, double>();
        private static readonly Dictionary<Control, FontFamily> _originalControlFontFamily = new Dictionary<Control, FontFamily>();
        private static readonly Dictionary<Control, double> _originalControlFontSize = new Dictionary<Control, double>();
        private static readonly Dictionary<Control, FontStyle> _originalControlFontStyle = new Dictionary<Control, FontStyle>();
        private static readonly Dictionary<Control, FontWeight> _originalControlFontWeight = new Dictionary<Control, FontWeight>();
        private static readonly Dictionary<TextBlock, FontFamily> _originalTextBlockFontFamily = new Dictionary<TextBlock, FontFamily>();
        private static readonly Dictionary<TextBlock, double> _originalTextBlockFontSize = new Dictionary<TextBlock, double>();
        private static readonly Dictionary<TextBlock, FontStyle> _originalTextBlockFontStyle = new Dictionary<TextBlock, FontStyle>();
        private static readonly Dictionary<TextBlock, FontWeight> _originalTextBlockFontWeight = new Dictionary<TextBlock, FontWeight>();
        private static readonly Dictionary<TextBlock, TextAlignment> _originalTextBlockAlignment = new Dictionary<TextBlock, TextAlignment>();
        private static readonly Dictionary<TextBlock, TextDecorationCollection> _originalTextBlockDecorations = new Dictionary<TextBlock, TextDecorationCollection>();
        private static readonly Dictionary<TextBlock, TextEffectCollection> _originalTextBlockEffects = new Dictionary<TextBlock, TextEffectCollection>();
        private static readonly Dictionary<UIElement, int> _originalZIndex = new Dictionary<UIElement, int>();
        private static Brush _originalWindowBackground;
        private static bool _windowBackgroundCaptured = false;
        private static readonly List<FontFamily> _fontPool = Fonts.SystemFontFamilies.Where(f => !string.IsNullOrWhiteSpace(f.Source)).ToList();
        private static readonly FontWeight[] _fontWeights = new[]
        {
            FontWeights.Thin,
            FontWeights.ExtraLight,
            FontWeights.Light,
            FontWeights.Normal,
            FontWeights.Medium,
            FontWeights.SemiBold,
            FontWeights.Bold,
            FontWeights.ExtraBold,
            FontWeights.Black
        };

        protected override Result OnExecute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var commandData = data;
            UIApplication uiapp = commandData.Application;
            IntPtr mainWindowHandle = uiapp.MainWindowHandle;

            HwndSource hwndSource = HwndSource.FromHwnd(mainWindowHandle);
            Window mainWindow = hwndSource?.RootVisual as Window;
            if (mainWindow == null)
                return Result.Failed;

            // Gérer clic simple ou double-clic
            DateTime currentTime = DateTime.Now;
            _clickCount++;

            if ((currentTime - _lastClickTime).TotalMilliseconds <= DoubleClickThreshold && _clickCount >= 2)
            {
                // Double-clic détecté : on arrête la boucle et on réinitialise
                _clickCount = 0;
                _isRunning = false;
                _cts?.Cancel();
                ResetAllVisuals(mainWindow);
                return Result.Succeeded;
            }

            _lastClickTime = currentTime;

            // Clic simple : démarrer/arrêter les couleurs
            Task.Delay(DoubleClickThreshold).ContinueWith(_ =>
            {
                if (_clickCount == 1)
                {
                    _clickCount = 0; // Réinitialiser

                    _isRunning = !_isRunning;

                    if (_isRunning)
                    {
                        // Démarrer le changement des couleurs avec annulation
                        _cts = new CancellationTokenSource();
                        _colorChangeTask = Task.Run(() => ChangeColorsLoop(mainWindow, _cts.Token));
                    }
                    else
                    {
                        // Arrêt demandé
                        _cts?.Cancel();
                        mainWindow.Dispatcher.Invoke(() =>
                        {
                            ResetAllVisuals(mainWindow);
                        });
                    }
                }
            });

            return Result.Succeeded;
        }

        private void ChangeColorsLoop(Window mainWindow, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(500); // Pause de 0.5 seconde

                mainWindow.Dispatcher.Invoke(() =>
                {
                    UpdateAllElementColors(mainWindow);
                });
            }
        }

        private void UpdateAllElementColors(Window mainWindow)
        {
            if (!_windowBackgroundCaptured)
            {
                _originalWindowBackground = mainWindow.Background;
                _windowBackgroundCaptured = true;
            }

            mainWindow.Background = GenerateRandomBrush();

            var allElements = GetAllDescendants(mainWindow);
            foreach (var element in allElements)
            {
                switch (element)
                {
                    case Border border:
                        ApplyBorderColors(border);
                        break;
                    case Panel panel:
                        ApplyPanelColors(panel);
                        break;
                    case Control control:
                        ApplyControlColors(control);
                        break;
                    case TextBlock textBlock:
                        ApplyTextBlockColors(textBlock);
                        break;
                    case Shape shape:
                        ApplyShapeColors(shape);
                        break;
                }
            }
        }

        private void ResetAllVisuals(Window mainWindow)
        {
            // S'assurer que la boucle de changement de couleur est arrêtée
            _isRunning = false;
            _cts?.Cancel();
            if (_windowBackgroundCaptured)
            {
                if (_originalWindowBackground != null)
                {
                    mainWindow.Background = _originalWindowBackground;
                }
                else
                {
                    mainWindow.ClearValue(Window.BackgroundProperty);
                }
                _windowBackgroundCaptured = false;
            }

            var allElements = GetAllDescendants(mainWindow);
            foreach (var element in allElements)
            {
                switch (element)
                {
                    case Border border:
                        RestoreBorder(border);
                        break;
                    case Panel panel:
                        RestorePanel(panel);
                        break;
                    case Control control:
                        RestoreControl(control);
                        break;
                    case TextBlock textBlock:
                        RestoreTextBlock(textBlock);
                        break;
                    case Shape shape:
                        RestoreShape(shape);
                        break;
                }
            }

            _originalBorderBackground.Clear();
            _originalBorderBrush.Clear();
            _originalBorderThickness.Clear();
            _originalPanelBackground.Clear();
            _originalControlBackground.Clear();
            _originalControlForeground.Clear();
            _originalControlBorderBrush.Clear();
            _originalTextBlockBackground.Clear();
            _originalTextBlockForeground.Clear();
            _originalShapeFill.Clear();
            _originalShapeStroke.Clear();
            _originalShapeDashArray.Clear();
            _originalShapeStrokeThickness.Clear();
            _originalRenderTransform.Clear();
            _originalLayoutTransform.Clear();
            _originalRenderTransformOrigin.Clear();
            _originalEffects.Clear();
            _originalOpacity.Clear();
            _originalControlFontFamily.Clear();
            _originalControlFontSize.Clear();
            _originalControlFontStyle.Clear();
            _originalControlFontWeight.Clear();
            _originalTextBlockFontFamily.Clear();
            _originalTextBlockFontSize.Clear();
            _originalTextBlockFontStyle.Clear();
            _originalTextBlockFontWeight.Clear();
            _originalTextBlockAlignment.Clear();
            _originalTextBlockDecorations.Clear();
            _originalTextBlockEffects.Clear();
            _originalZIndex.Clear();
        }

        private void ApplyBorderColors(Border border)
        {
            if (!_originalBorderBackground.ContainsKey(border))
            {
                _originalBorderBackground[border] = border.Background;
            }

            if (!_originalBorderBrush.ContainsKey(border))
            {
                _originalBorderBrush[border] = border.BorderBrush;
            }

            if (!_originalBorderThickness.ContainsKey(border))
            {
                _originalBorderThickness[border] = border.BorderThickness;
            }

            ApplyTransformAndEffects(border);

            var background = GenerateRandomBrush();
            border.Background = CreatePsychedelicBackground(background);
            border.BorderBrush = GenerateVariantBrush(background, GetVariantFactor(background));
            border.BorderThickness = new Thickness(_rnd.Next(1, 6));
        }

        private void ApplyPanelColors(Panel panel)
        {
            if (!_originalPanelBackground.ContainsKey(panel))
            {
                _originalPanelBackground[panel] = panel.Background;
            }

            if (!_originalZIndex.ContainsKey(panel))
            {
                _originalZIndex[panel] = Panel.GetZIndex(panel);
            }

            ApplyTransformAndEffects(panel);

            var background = GenerateRandomBrush();
            panel.Background = CreatePsychedelicBackground(background);
            Panel.SetZIndex(panel, _rnd.Next(-50, 50));
        }

        private void ApplyControlColors(Control control)
        {
            if (!_originalControlBackground.ContainsKey(control))
            {
                _originalControlBackground[control] = control.Background;
            }

            if (!_originalControlForeground.ContainsKey(control))
            {
                _originalControlForeground[control] = control.Foreground;
            }

            if (!_originalControlBorderBrush.ContainsKey(control))
            {
                _originalControlBorderBrush[control] = control.BorderBrush;
            }

            if (!_originalControlFontFamily.ContainsKey(control))
            {
                _originalControlFontFamily[control] = control.FontFamily;
            }

            if (!_originalControlFontSize.ContainsKey(control))
            {
                _originalControlFontSize[control] = control.FontSize;
            }

            if (!_originalControlFontStyle.ContainsKey(control))
            {
                _originalControlFontStyle[control] = control.FontStyle;
            }

            if (!_originalControlFontWeight.ContainsKey(control))
            {
                _originalControlFontWeight[control] = control.FontWeight;
            }

            ApplyTransformAndEffects(control);

            var background = GenerateRandomBrush();
            control.Background = CreatePsychedelicBackground(background);
            control.Foreground = GenerateMulticolorTextBrush(background.Color);
            control.BorderBrush = GenerateVariantBrush(background, GetVariantFactor(background));
            control.FontFamily = GetRandomFontFamily();
            control.FontSize = GetRandomFontSize(control.FontSize);
            control.FontStyle = GetRandomFontStyle();
            control.FontWeight = GetRandomFontWeight();
        }

        private void ApplyTextBlockColors(TextBlock textBlock)
        {
            if (!_originalTextBlockBackground.ContainsKey(textBlock))
            {
                _originalTextBlockBackground[textBlock] = textBlock.Background;
            }

            if (!_originalTextBlockForeground.ContainsKey(textBlock))
            {
                _originalTextBlockForeground[textBlock] = textBlock.Foreground;
            }

            if (!_originalTextBlockFontFamily.ContainsKey(textBlock))
            {
                _originalTextBlockFontFamily[textBlock] = textBlock.FontFamily;
            }

            if (!_originalTextBlockFontSize.ContainsKey(textBlock))
            {
                _originalTextBlockFontSize[textBlock] = textBlock.FontSize;
            }

            if (!_originalTextBlockFontStyle.ContainsKey(textBlock))
            {
                _originalTextBlockFontStyle[textBlock] = textBlock.FontStyle;
            }

            if (!_originalTextBlockFontWeight.ContainsKey(textBlock))
            {
                _originalTextBlockFontWeight[textBlock] = textBlock.FontWeight;
            }

            if (!_originalTextBlockAlignment.ContainsKey(textBlock))
            {
                _originalTextBlockAlignment[textBlock] = textBlock.TextAlignment;
            }

            if (!_originalTextBlockDecorations.ContainsKey(textBlock))
            {
                _originalTextBlockDecorations[textBlock] = textBlock.TextDecorations;
            }

            if (!_originalTextBlockEffects.ContainsKey(textBlock))
            {
                _originalTextBlockEffects[textBlock] = textBlock.TextEffects;
            }

            ApplyTransformAndEffects(textBlock);

            var background = GenerateRandomBrush();
            textBlock.Background = CreatePsychedelicBackground(background);
            textBlock.Foreground = GenerateMulticolorTextBrush(background.Color);
            textBlock.FontFamily = GetRandomFontFamily();
            textBlock.FontSize = GetRandomFontSize(textBlock.FontSize);
            textBlock.FontStyle = GetRandomFontStyle();
            textBlock.FontWeight = GetRandomFontWeight();
            textBlock.TextAlignment = GetRandomTextAlignment();
            textBlock.TextDecorations = GetRandomTextDecorations();
            textBlock.TextEffects = CreateRandomTextEffects();
        }

        private void ApplyShapeColors(Shape shape)
        {
            if (!_originalShapeFill.ContainsKey(shape))
            {
                _originalShapeFill[shape] = shape.Fill;
            }

            if (!_originalShapeStroke.ContainsKey(shape))
            {
                _originalShapeStroke[shape] = shape.Stroke;
            }

            if (!_originalShapeDashArray.ContainsKey(shape))
            {
                _originalShapeDashArray[shape] = shape.StrokeDashArray;
            }

            if (!_originalShapeStrokeThickness.ContainsKey(shape))
            {
                _originalShapeStrokeThickness[shape] = shape.StrokeThickness;
            }

            ApplyTransformAndEffects(shape);

            var fill = GenerateRandomBrush();
            shape.Fill = CreatePsychedelicBackground(fill);
            shape.Stroke = GenerateContrastingBrush(fill);
            shape.StrokeThickness = _rnd.NextDouble() * 8 + 1;
            shape.StrokeDashArray = CreateRandomDashArray();
        }

        private void RestoreBorder(Border border)
        {
            if (_originalBorderBackground.TryGetValue(border, out Brush originalBackground))
            {
                if (originalBackground != null)
                {
                    border.Background = originalBackground;
                }
                else
                {
                    border.ClearValue(Border.BackgroundProperty);
                }
            }
            else
            {
                border.ClearValue(Border.BackgroundProperty);
            }

            if (_originalBorderBrush.TryGetValue(border, out Brush originalBorderBrush))
            {
                if (originalBorderBrush != null)
                {
                    border.BorderBrush = originalBorderBrush;
                }
                else
                {
                    border.ClearValue(Border.BorderBrushProperty);
                }
            }
            else
            {
                border.ClearValue(Border.BorderBrushProperty);
            }

            if (_originalBorderThickness.TryGetValue(border, out Thickness originalThickness))
            {
                border.BorderThickness = originalThickness;
            }
            else
            {
                border.ClearValue(Border.BorderThicknessProperty);
            }

            RestoreTransformAndEffects(border);
        }

        private void RestorePanel(Panel panel)
        {
            if (_originalPanelBackground.TryGetValue(panel, out Brush originalBackground))
            {
                if (originalBackground != null)
                {
                    panel.Background = originalBackground;
                }
                else
                {
                    panel.ClearValue(Panel.BackgroundProperty);
                }
            }
            else
            {
                panel.ClearValue(Panel.BackgroundProperty);
            }

            if (_originalZIndex.TryGetValue(panel, out int originalZ))
            {
                Panel.SetZIndex(panel, originalZ);
            }

            RestoreTransformAndEffects(panel);
        }

        private void RestoreControl(Control control)
        {
            if (_originalControlBackground.TryGetValue(control, out Brush originalBackground))
            {
                if (originalBackground != null)
                {
                    control.Background = originalBackground;
                }
                else
                {
                    control.ClearValue(Control.BackgroundProperty);
                }
            }
            else
            {
                control.ClearValue(Control.BackgroundProperty);
            }

            if (_originalControlForeground.TryGetValue(control, out Brush originalForeground))
            {
                if (originalForeground != null)
                {
                    control.Foreground = originalForeground;
                }
                else
                {
                    control.ClearValue(Control.ForegroundProperty);
                }
            }
            else
            {
                control.ClearValue(Control.ForegroundProperty);
            }

            if (_originalControlBorderBrush.TryGetValue(control, out Brush originalBorderBrush))
            {
                if (originalBorderBrush != null)
                {
                    control.BorderBrush = originalBorderBrush;
                }
                else
                {
                    control.ClearValue(Control.BorderBrushProperty);
                }
            }
            else
            {
                control.ClearValue(Control.BorderBrushProperty);
            }

            if (_originalControlFontFamily.TryGetValue(control, out FontFamily originalFontFamily))
            {
                control.FontFamily = originalFontFamily;
            }
            else
            {
                control.ClearValue(Control.FontFamilyProperty);
            }

            if (_originalControlFontSize.TryGetValue(control, out double originalFontSize))
            {
                control.FontSize = originalFontSize;
            }
            else
            {
                control.ClearValue(Control.FontSizeProperty);
            }

            if (_originalControlFontStyle.TryGetValue(control, out FontStyle originalFontStyle))
            {
                control.FontStyle = originalFontStyle;
            }
            else
            {
                control.ClearValue(Control.FontStyleProperty);
            }

            if (_originalControlFontWeight.TryGetValue(control, out FontWeight originalFontWeight))
            {
                control.FontWeight = originalFontWeight;
            }
            else
            {
                control.ClearValue(Control.FontWeightProperty);
            }

            RestoreTransformAndEffects(control);
        }

        private void RestoreTextBlock(TextBlock textBlock)
        {
            if (_originalTextBlockBackground.TryGetValue(textBlock, out Brush originalBackground))
            {
                if (originalBackground != null)
                {
                    textBlock.Background = originalBackground;
                }
                else
                {
                    textBlock.ClearValue(TextBlock.BackgroundProperty);
                }
            }
            else
            {
                textBlock.ClearValue(TextBlock.BackgroundProperty);
            }

            if (_originalTextBlockForeground.TryGetValue(textBlock, out Brush originalForeground))
            {
                if (originalForeground != null)
                {
                    textBlock.Foreground = originalForeground;
                }
                else
                {
                    textBlock.ClearValue(TextBlock.ForegroundProperty);
                }
            }
            else
            {
                textBlock.ClearValue(TextBlock.ForegroundProperty);
            }

            if (_originalTextBlockFontFamily.TryGetValue(textBlock, out FontFamily originalFontFamily))
            {
                textBlock.FontFamily = originalFontFamily;
            }
            else
            {
                textBlock.ClearValue(TextBlock.FontFamilyProperty);
            }

            if (_originalTextBlockFontSize.TryGetValue(textBlock, out double originalFontSize))
            {
                textBlock.FontSize = originalFontSize;
            }
            else
            {
                textBlock.ClearValue(TextBlock.FontSizeProperty);
            }

            if (_originalTextBlockFontStyle.TryGetValue(textBlock, out FontStyle originalFontStyle))
            {
                textBlock.FontStyle = originalFontStyle;
            }
            else
            {
                textBlock.ClearValue(TextBlock.FontStyleProperty);
            }

            if (_originalTextBlockFontWeight.TryGetValue(textBlock, out FontWeight originalFontWeight))
            {
                textBlock.FontWeight = originalFontWeight;
            }
            else
            {
                textBlock.ClearValue(TextBlock.FontWeightProperty);
            }

            if (_originalTextBlockAlignment.TryGetValue(textBlock, out TextAlignment originalAlignment))
            {
                textBlock.TextAlignment = originalAlignment;
            }
            else
            {
                textBlock.ClearValue(TextBlock.TextAlignmentProperty);
            }

            if (_originalTextBlockDecorations.TryGetValue(textBlock, out TextDecorationCollection originalDecorations))
            {
                textBlock.TextDecorations = originalDecorations;
            }
            else
            {
                textBlock.ClearValue(TextBlock.TextDecorationsProperty);
            }

            if (_originalTextBlockEffects.TryGetValue(textBlock, out TextEffectCollection originalEffects))
            {
                textBlock.TextEffects = originalEffects;
            }
            else
            {
                textBlock.ClearValue(TextBlock.TextEffectsProperty);
            }

            RestoreTransformAndEffects(textBlock);
        }

        private void RestoreShape(Shape shape)
        {
            if (_originalShapeFill.TryGetValue(shape, out Brush originalFill))
            {
                if (originalFill != null)
                {
                    shape.Fill = originalFill;
                }
                else
                {
                    shape.ClearValue(Shape.FillProperty);
                }
            }
            else
            {
                shape.ClearValue(Shape.FillProperty);
            }

            if (_originalShapeStroke.TryGetValue(shape, out Brush originalStroke))
            {
                if (originalStroke != null)
                {
                    shape.Stroke = originalStroke;
                }
                else
                {
                    shape.ClearValue(Shape.StrokeProperty);
                }
            }
            else
            {
                shape.ClearValue(Shape.StrokeProperty);
            }

            if (_originalShapeDashArray.TryGetValue(shape, out DoubleCollection originalDashArray))
            {
                shape.StrokeDashArray = originalDashArray;
            }
            else
            {
                shape.ClearValue(Shape.StrokeDashArrayProperty);
            }

            if (_originalShapeStrokeThickness.TryGetValue(shape, out double originalStrokeThickness))
            {
                shape.StrokeThickness = originalStrokeThickness;
            }
            else
            {
                shape.ClearValue(Shape.StrokeThicknessProperty);
            }

            RestoreTransformAndEffects(shape);
        }

        private List<DependencyObject> GetAllDescendants(DependencyObject parent)
        {
            var found = new List<DependencyObject>();
            if (parent == null)
                return found;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                found.Add(child);
                found.AddRange(GetAllDescendants(child));
            }

            return found;
        }

        private void ApplyTransformAndEffects(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            if (!_originalRenderTransform.ContainsKey(element))
            {
                _originalRenderTransform[element] = element.RenderTransform;
            }

            if (!_originalLayoutTransform.ContainsKey(element))
            {
                _originalLayoutTransform[element] = element.LayoutTransform;
            }

            if (!_originalRenderTransformOrigin.ContainsKey(element))
            {
                _originalRenderTransformOrigin[element] = element.RenderTransformOrigin;
            }

            if (!_originalEffects.ContainsKey(element))
            {
                _originalEffects[element] = element.Effect;
            }

            if (!_originalOpacity.ContainsKey(element))
            {
                _originalOpacity[element] = element.Opacity;
            }

            element.RenderTransformOrigin = new Point(0.5, 0.5);

            var renderGroup = new TransformGroup();
            renderGroup.Children.Add(new RotateTransform(_rnd.NextDouble() * 720 - 360));
            renderGroup.Children.Add(new ScaleTransform(0.5 + _rnd.NextDouble() * 2.5, 0.5 + _rnd.NextDouble() * 2.5));

            if (_rnd.NextDouble() < 0.85)
            {
                renderGroup.Children.Add(new SkewTransform(_rnd.Next(-45, 46), _rnd.Next(-45, 46)));
            }

            element.RenderTransform = renderGroup;

            if (_rnd.NextDouble() < 0.5)
            {
                element.LayoutTransform = new RotateTransform(_rnd.NextDouble() * 120 - 60);
            }
            else
            {
                element.LayoutTransform = new ScaleTransform(0.7 + _rnd.NextDouble() * 2.2, 0.7 + _rnd.NextDouble() * 2.2);
            }

            element.Opacity = 0.4 + _rnd.NextDouble() * 0.6;
            element.Effect = CreateNeonGlowEffect();
        }

        private void RestoreTransformAndEffects(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            if (_originalRenderTransform.TryGetValue(element, out Transform originalRenderTransform))
            {
                element.RenderTransform = originalRenderTransform;
            }
            else
            {
                element.ClearValue(UIElement.RenderTransformProperty);
            }

            if (_originalLayoutTransform.TryGetValue(element, out Transform originalLayoutTransform))
            {
                element.LayoutTransform = originalLayoutTransform;
            }
            else
            {
                element.ClearValue(FrameworkElement.LayoutTransformProperty);
            }

            if (_originalRenderTransformOrigin.TryGetValue(element, out Point originalOrigin))
            {
                element.RenderTransformOrigin = originalOrigin;
            }
            else
            {
                element.ClearValue(UIElement.RenderTransformOriginProperty);
            }

            if (_originalEffects.TryGetValue(element, out Effect originalEffect))
            {
                element.Effect = originalEffect;
            }
            else
            {
                element.ClearValue(UIElement.EffectProperty);
            }

            if (_originalOpacity.TryGetValue(element, out double originalOpacity))
            {
                element.Opacity = originalOpacity;
            }
            else
            {
                element.ClearValue(UIElement.OpacityProperty);
            }
        }

        private Effect CreateNeonGlowEffect()
        {
            var glowColor = GenerateRandomBrush().Color;
            return new DropShadowEffect
            {
                Color = glowColor,
                BlurRadius = 20 + _rnd.NextDouble() * 40,
                ShadowDepth = _rnd.NextDouble() * 20,
                Direction = _rnd.Next(0, 360),
                Opacity = 0.6 + _rnd.NextDouble() * 0.4
            };
        }

        private Brush CreatePsychedelicBackground(SolidColorBrush baseBrush)
        {
            double mode = _rnd.NextDouble();

            if (mode < 0.4)
            {
                return CreateLinearExplosion(baseBrush.Color);
            }

            if (mode < 0.75)
            {
                return CreateRadialBlast(baseBrush.Color);
            }

            if (mode < 0.92)
            {
                return CreatePatternBrush(baseBrush.Color);
            }

            return baseBrush;
        }

        private Brush CreateLinearExplosion(Color baseColor)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(_rnd.NextDouble(), 0),
                EndPoint = new Point(_rnd.NextDouble(), 1)
            };

            int stopCount = _rnd.Next(3, 9);
            var palette = GenerateContrastingPalette(baseColor, stopCount);

            for (int i = 0; i < palette.Count; i++)
            {
                double offset = (double)i / (palette.Count - 1);
                gradient.GradientStops.Add(new GradientStop(palette[i], offset));
            }

            if (gradient.CanFreeze)
            {
                gradient.Freeze();
            }

            return gradient;
        }

        private Brush CreateRadialBlast(Color baseColor)
        {
            var gradient = new RadialGradientBrush
            {
                GradientOrigin = new Point(_rnd.NextDouble(), _rnd.NextDouble()),
                Center = new Point(_rnd.NextDouble(), _rnd.NextDouble()),
                RadiusX = 0.4 + _rnd.NextDouble() * 0.6,
                RadiusY = 0.4 + _rnd.NextDouble() * 0.6
            };

            int stopCount = _rnd.Next(4, 8);
            var palette = GenerateContrastingPalette(baseColor, stopCount);

            for (int i = 0; i < palette.Count; i++)
            {
                gradient.GradientStops.Add(new GradientStop(palette[i], (double)i / (palette.Count - 1)));
            }

            if (gradient.CanFreeze)
            {
                gradient.Freeze();
            }

            return gradient;
        }

        private Brush CreatePatternBrush(Color baseColor)
        {
            var drawingGroup = new DrawingGroup();

            drawingGroup.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

            int shapeCount = _rnd.Next(4, 12);
            for (int i = 0; i < shapeCount; i++)
            {
                var geometry = new EllipseGeometry(new Point(_rnd.NextDouble(), _rnd.NextDouble()), _rnd.NextDouble() * 0.6, _rnd.NextDouble() * 0.6);
                var brush = GenerateRandomBrush();
                var pen = new Pen(GenerateContrastingBrush(brush), _rnd.NextDouble() * 0.3);
                drawingGroup.Children.Add(new GeometryDrawing(brush, pen, geometry));
            }

            var brushResult = new DrawingBrush(drawingGroup)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 0.25 + _rnd.NextDouble() * 0.5, 0.25 + _rnd.NextDouble() * 0.5),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill
            };

            if (brushResult.CanFreeze)
            {
                brushResult.Freeze();
            }

            return brushResult;
        }

        private FontFamily GetRandomFontFamily()
        {
            if (_fontPool.Count == 0)
            {
                return new FontFamily("Comic Sans MS");
            }

            return _fontPool[_rnd.Next(_fontPool.Count)];
        }

        private double GetRandomFontSize(double baseline)
        {
            double baseSize = double.IsNaN(baseline) || baseline <= 0 ? 12 : baseline;
            double multiplier = 0.6 + _rnd.NextDouble() * 3.2;
            return Math.Max(6, baseSize * multiplier);
        }

        private FontStyle GetRandomFontStyle()
        {
            switch (_rnd.Next(0, 3))
            {
                case 0:
                    return FontStyles.Normal;
                case 1:
                    return FontStyles.Italic;
                default:
                    return FontStyles.Oblique;
            }
        }

        private FontWeight GetRandomFontWeight()
        {
            return _fontWeights[_rnd.Next(_fontWeights.Length)];
        }

        private TextAlignment GetRandomTextAlignment()
        {
            var values = new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Justify };
            return values[_rnd.Next(values.Length)];
        }

        private TextDecorationCollection GetRandomTextDecorations()
        {
            if (_rnd.NextDouble() < 0.35)
            {
                return null;
            }

            var decorations = new TextDecorationCollection();

            if (_rnd.NextDouble() < 0.7)
            {
                AddTextDecorationRange(decorations, TextDecorations.Underline);
            }

            if (_rnd.NextDouble() < 0.5)
            {
                AddTextDecorationRange(decorations, TextDecorations.OverLine);
            }

            if (_rnd.NextDouble() < 0.4)
            {
                AddTextDecorationRange(decorations, TextDecorations.Strikethrough);
            }

            if (_rnd.NextDouble() < 0.3)
            {
                AddTextDecorationRange(decorations, TextDecorations.Baseline);
            }

            return decorations;
        }

        private void AddTextDecorationRange(TextDecorationCollection target, TextDecorationCollection source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var decoration in source)
            {
                target.Add(decoration);
            }
        }

        private TextEffectCollection CreateRandomTextEffects()
        {
            if (_rnd.NextDouble() < 0.4)
            {
                return null;
            }

            var collection = new TextEffectCollection();

            int effectCount = _rnd.Next(1, 4);
            for (int i = 0; i < effectCount; i++)
            {
                var effect = new TextEffect
                {
                    Foreground = GenerateMulticolorTextBrush(GenerateRandomBrush().Color),
                    PositionStart = 0,
                    PositionCount = int.MaxValue,
                    Transform = new SkewTransform(_rnd.Next(-40, 41), _rnd.Next(-40, 41))
                };

                collection.Add(effect);
            }

            return collection;
        }

        private DoubleCollection CreateRandomDashArray()
        {
            if (_rnd.NextDouble() < 0.5)
            {
                return null;
            }

            int count = _rnd.Next(2, 6);
            var collection = new DoubleCollection();
            for (int i = 0; i < count; i++)
            {
                collection.Add(0.5 + _rnd.NextDouble() * 6);
            }

            return collection;
        }

        private SolidColorBrush GenerateRandomBrush()
        {
            byte r = (byte)_rnd.Next(0, 256);
            byte g = (byte)_rnd.Next(0, 256);
            byte b = (byte)_rnd.Next(0, 256);
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }

        private SolidColorBrush GenerateContrastingBrush(SolidColorBrush background)
        {
            var color = background.Color;
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            return luminance > 0.5 ? new SolidColorBrush(Colors.Black) : new SolidColorBrush(Colors.White);
        }

        private SolidColorBrush GenerateVariantBrush(SolidColorBrush baseBrush, double factor)
        {
            var color = baseBrush.Color;
            byte r = ClampToByte(color.R * factor);
            byte g = ClampToByte(color.G * factor);
            byte b = ClampToByte(color.B * factor);
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }

        private Brush GenerateMulticolorTextBrush(Color backgroundColor)
        {
            int stopCount = _rnd.Next(3, 7);
            var palette = GenerateContrastingPalette(backgroundColor, stopCount);

            if (_rnd.NextDouble() < 0.5)
            {
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Point(_rnd.NextDouble(), 0),
                    EndPoint = new Point(_rnd.NextDouble(), 1)
                };

                for (int i = 0; i < palette.Count; i++)
                {
                    double offset = (double)i / (palette.Count - 1);
                    gradient.GradientStops.Add(new GradientStop(palette[i], offset));
                }

                gradient.SpreadMethod = (GradientSpreadMethod)_rnd.Next(0, 3);

                if (gradient.CanFreeze)
                {
                    gradient.Freeze();
                }

                return gradient;
            }
            else
            {
                var gradient = new RadialGradientBrush
                {
                    GradientOrigin = new Point(_rnd.NextDouble(), _rnd.NextDouble()),
                    Center = new Point(_rnd.NextDouble(), _rnd.NextDouble()),
                    RadiusX = 0.3 + _rnd.NextDouble() * 0.7,
                    RadiusY = 0.3 + _rnd.NextDouble() * 0.7
                };

                for (int i = 0; i < palette.Count; i++)
                {
                    gradient.GradientStops.Add(new GradientStop(palette[i], (double)i / (palette.Count - 1)));
                }

                gradient.SpreadMethod = (GradientSpreadMethod)_rnd.Next(0, 3);

                if (gradient.CanFreeze)
                {
                    gradient.Freeze();
                }

                return gradient;
            }
        }

        private List<Color> GenerateContrastingPalette(Color backgroundColor, int count)
        {
            var palette = new List<Color>(count);

            for (int i = 0; i < count; i++)
            {
                palette.Add(GenerateRandomContrastingColor(backgroundColor, palette, i));
            }

            return palette;
        }

        private Color GenerateRandomContrastingColor(Color backgroundColor, List<Color> palette, int index)
        {
            const double minimumContrast = 3.0;
            const double minimumDistance = 90.0;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                var candidate = Color.FromRgb((byte)_rnd.Next(0, 256), (byte)_rnd.Next(0, 256), (byte)_rnd.Next(0, 256));

                if (HasSufficientContrast(candidate, backgroundColor, minimumContrast) && IsDistinct(candidate, palette, minimumDistance))
                {
                    return candidate;
                }
            }

            return GenerateFallbackColor(backgroundColor, palette, index);
        }

        private bool HasSufficientContrast(Color foreground, Color background, double threshold)
        {
            double ratio = GetContrastRatio(foreground, background);
            return ratio >= threshold;
        }

        private double GetContrastRatio(Color first, Color second)
        {
            double luminanceFirst = GetRelativeLuminance(first);
            double luminanceSecond = GetRelativeLuminance(second);
            double brighter = Math.Max(luminanceFirst, luminanceSecond);
            double darker = Math.Min(luminanceFirst, luminanceSecond);
            return (brighter + 0.05) / (darker + 0.05);
        }

        private double GetRelativeLuminance(Color color)
        {
            double r = NormalizeChannel(color.R / 255.0);
            double g = NormalizeChannel(color.G / 255.0);
            double b = NormalizeChannel(color.B / 255.0);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private double NormalizeChannel(double value)
        {
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private bool IsDistinct(Color candidate, List<Color> palette, double minimumDistance)
        {
            foreach (var existing in palette)
            {
                if (GetColorDistance(existing, candidate) < minimumDistance)
                {
                    return false;
                }
            }

            return true;
        }

        private double GetColorDistance(Color first, Color second)
        {
            int dr = first.R - second.R;
            int dg = first.G - second.G;
            int db = first.B - second.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private Color GenerateFallbackColor(Color backgroundColor, List<Color> palette, int index)
        {
            var inverted = Color.FromRgb((byte)(255 - backgroundColor.R), (byte)(255 - backgroundColor.G), (byte)(255 - backgroundColor.B));
            var reference = index % 2 == 0 ? Colors.White : Colors.Black;
            double mix = Math.Min(0.85, 0.35 + palette.Count * 0.18);
            return BlendColors(inverted, reference, mix);
        }

        private Color BlendColors(Color source, Color target, double amount)
        {
            byte r = (byte)(source.R + (target.R - source.R) * amount);
            byte g = (byte)(source.G + (target.G - source.G) * amount);
            byte b = (byte)(source.B + (target.B - source.B) * amount);
            return Color.FromRgb(r, g, b);
        }

        private double GetVariantFactor(SolidColorBrush brush)
        {
            var color = brush.Color;
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
            return luminance > 0.5 ? 0.6 : 1.4;
        }

        private byte ClampToByte(double value)
        {
            if (value < 0)
                return 0;

            if (value > 255)
                return 255;

            return (byte)value;
        }
    }
}