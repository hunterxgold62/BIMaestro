using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyRevitTroll
{
    [Transaction(TransactionMode.Manual)]
    public class CommandHeartsParam : IExternalCommand
    {
        // Indique si le mode est actif
        private static bool _modeActive = false;
        // Timer pour exécuter l'affichage en arrière-plan
        private static Timer _timer = null;
        // Compteur pour alterner les séquences
        private static int _sequenceIndex = 0;

        // Nous avons maintenant 10 séquences
        private static int _totalSequences = 10;

        // Facteur global d'agrandissement
        private static double scaleFactor = 1.5;

        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            if (!_modeActive)
            {
                _modeActive = true;
                TaskDialog.Show("Troll Shapes",
                    "Mode Troll activé : 10 formes différentes (agrandies à 1,5×) vont apparaître en boucle.\n" +
                    "Recliquez sur le bouton pour désactiver.");

                // Démarre immédiatement le timer puis toutes les 3 secondes
                _timer = new Timer(TimerCallback, null, 0, 3000);
            }
            else
            {
                _modeActive = false;
                _timer?.Dispose();
                _timer = null;
                TaskDialog.Show("Troll Shapes", "Mode Troll désactivé.");
            }

            return Result.Succeeded;
        }

        // Méthode appelée par le timer (sur un thread de fond)
        private void TimerCallback(object state)
        {
            if (!_modeActive) return;
            // On invoque la création des fenêtres sur le thread UI
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowSequence();
            }));
        }

        // Alterne entre les séquences
        private void ShowSequence()
        {
            switch (_sequenceIndex)
            {
                case 0:
                    ShowHeartShape();
                    break;
                case 1:
                    ShowSunWithRays();
                    break;
                case 2:
                    ShowStarShape();
                    break;
                case 3:
                    ShowFlowerShape();
                    break;
                case 4:
                    ShowSpiralShape();
                    break;
                case 5:
                    ShowDiamondShape();
                    break;
                case 6:
                    ShowTriangleShape();
                    break;
                case 7:
                    ShowWaveShape();
                    break;
                case 8:
                    ShowSquareShape();
                    break;
                case 9:
                    ShowInfinityShape();
                    break;
            }

            // Passe à la séquence suivante
            _sequenceIndex = (_sequenceIndex + 1) % _totalSequences;
        }

        // -----------------------------------------------------------------------------------
        // 1) CŒUR
        // -----------------------------------------------------------------------------------
        private void ShowHeartShape()
        {
            int numberOfPoints = 30;
            double scale = 3.0 * scaleFactor;      // échelle du cœur
            double pixelScale = 5.0 * scaleFactor; // échelle en pixels

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double offsetX = screenWidth / 2;
            double offsetY = screenHeight / 2;

            for (int i = 0; i < numberOfPoints; i++)
            {
                double t = 2.0 * Math.PI * i / (numberOfPoints - 1);
                double sinT = Math.Sin(t);
                double cosT = Math.Cos(t);

                double xVal = 16.0 * Math.Pow(sinT, 3);
                double yVal = 13.0 * cosT - 5.0 * Math.Cos(2 * t)
                              - 2.0 * Math.Cos(3 * t) - Math.Cos(4 * t);

                xVal *= scale * pixelScale;
                yVal *= -scale * pixelScale;

                double finalX = offsetX + xVal;
                double finalY = offsetY + yVal;

                // Fenêtre (on peut l’agrandir un peu si on veut)
                double winSize = 100 * scaleFactor;

                TrollWindow tw = new TrollWindow(
                    "Je t'adore !",
                    System.Windows.Media.Brushes.Pink,
                    System.Windows.Media.Brushes.Red,
                    width: winSize,
                    height: winSize);
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = finalX - (winSize / 2);
                tw.Top = finalY - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 2) SOLEIL + RAYONS
        // -----------------------------------------------------------------------------------
        private void ShowSunWithRays()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            // Centre du soleil (un grand cercle)
            double centerSunSize = 200 * scaleFactor;
            TrollWindow centerSun = new TrollWindow(
                "Tu es lumineux !",
                System.Windows.Media.Brushes.Yellow,
                System.Windows.Media.Brushes.DarkOrange,
                width: centerSunSize,
                height: centerSunSize
            );
            centerSun.WindowStartupLocation = WindowStartupLocation.Manual;
            centerSun.Left = centerX - (centerSun.Width / 2);
            centerSun.Top = centerY - (centerSun.Height / 2);
            centerSun.Show();
            CloseAfterDelay(centerSun, 2000);

            // Rayons
            int rayCount = 8;
            double distanceRay = 200 * scaleFactor;
            double rayWidth = 80 * scaleFactor;
            double rayHeight = 80 * scaleFactor;

            for (int i = 0; i < rayCount; i++)
            {
                double angle = 2.0 * Math.PI * i / rayCount;
                double rayX = centerX + distanceRay * Math.Cos(angle);
                double rayY = centerY + distanceRay * Math.Sin(angle);

                TrollWindow ray = new TrollWindow(
                    "",
                    System.Windows.Media.Brushes.Yellow,
                    System.Windows.Media.Brushes.DarkOrange,
                    width: rayWidth,
                    height: rayHeight
                );
                ray.WindowStartupLocation = WindowStartupLocation.Manual;
                ray.Left = rayX - (rayWidth / 2);
                ray.Top = rayY - (rayHeight / 2);
                ray.Show();
                CloseAfterDelay(ray, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 3) ÉTOILE à 5 branches
        // -----------------------------------------------------------------------------------
        private void ShowStarShape()
        {
            int branches = 5;
            double outerRadius = 150 * scaleFactor;
            double innerRadius = 60 * scaleFactor;
            int totalPoints = branches * 2; // 10 points

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            double winSize = 80 * scaleFactor;

            for (int i = 0; i < totalPoints; i++)
            {
                double radius = (i % 2 == 0) ? outerRadius : innerRadius;
                // Décalage pour orienter la branche vers le haut
                double angle = Math.PI / 2 * 3 + (2.0 * Math.PI * i / totalPoints);

                double x = centerX + radius * Math.Cos(angle);
                double y = centerY + radius * Math.Sin(angle);

                TrollWindow tw = new TrollWindow(
                    "Tu es exceptionnel !",
                    System.Windows.Media.Brushes.LightBlue,
                    System.Windows.Media.Brushes.Blue,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = x - (winSize / 2);
                tw.Top = y - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 4) FLEUR (un centre + pétales autour)
        // -----------------------------------------------------------------------------------
        private void ShowFlowerShape()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            // Centre de la fleur
            double centerSize = 100 * scaleFactor;
            TrollWindow centerFlower = new TrollWindow(
                "Tu fleuris la vie !",
                System.Windows.Media.Brushes.Magenta,
                System.Windows.Media.Brushes.Pink,
                width: centerSize,
                height: centerSize
            );
            centerFlower.WindowStartupLocation = WindowStartupLocation.Manual;
            centerFlower.Left = centerX - (centerSize / 2);
            centerFlower.Top = centerY - (centerSize / 2);
            centerFlower.Show();
            CloseAfterDelay(centerFlower, 2000);

            // Pétales
            int petalCount = 8;
            double distancePetal = 120 * scaleFactor;
            double petalWidth = 60 * scaleFactor;
            double petalHeight = 60 * scaleFactor;

            for (int i = 0; i < petalCount; i++)
            {
                double angle = 2.0 * Math.PI * i / petalCount;
                double px = centerX + distancePetal * Math.Cos(angle);
                double py = centerY + distancePetal * Math.Sin(angle);

                TrollWindow petal = new TrollWindow(
                    "", // pas de texte
                    System.Windows.Media.Brushes.Pink,
                    System.Windows.Media.Brushes.Red,
                    width: petalWidth,
                    height: petalHeight
                );
                petal.WindowStartupLocation = WindowStartupLocation.Manual;
                petal.Left = px - (petalWidth / 2);
                petal.Top = py - (petalHeight / 2);
                petal.Show();
                CloseAfterDelay(petal, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 5) SPIRALE (r = a + bθ)
        // -----------------------------------------------------------------------------------
        private void ShowSpiralShape()
        {
            int pointCount = 40;
            // On multiplie a et b par scaleFactor
            double a = 5 * scaleFactor;
            double b = 10 * scaleFactor;
            double angleStep = 0.3;

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            double theta = 0;

            double winSize = 80 * scaleFactor;

            for (int i = 0; i < pointCount; i++)
            {
                double r = a + b * theta;
                double x = centerX + r * Math.Cos(theta);
                double y = centerY + r * Math.Sin(theta);

                TrollWindow tw = new TrollWindow(
                    "Toujours en mouvement !",
                    System.Windows.Media.Brushes.LightGreen,
                    System.Windows.Media.Brushes.Green,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = x - (winSize / 2);
                tw.Top = y - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);

                theta += angleStep;
            }
        }

        // -----------------------------------------------------------------------------------
        // 6) DIAMANT (4 points, un losange)
        // -----------------------------------------------------------------------------------
        private void ShowDiamondShape()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            // demi-diagonale
            double size = 100 * scaleFactor;
            double winSize = 80 * scaleFactor;

            // Les 4 sommets du diamant (haut, droite, bas, gauche)
            double[,] points = new double[,]
            {
                { centerX,        centerY - size },
                { centerX + size, centerY        },
                { centerX,        centerY + size },
                { centerX - size, centerY        }
            };

            for (int i = 0; i < 4; i++)
            {
                double px = points[i, 0];
                double py = points[i, 1];

                TrollWindow tw = new TrollWindow(
                    "Tu brilles de mille feux !",
                    System.Windows.Media.Brushes.Lavender,
                    System.Windows.Media.Brushes.Purple,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = px - (winSize / 2);
                tw.Top = py - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 7) TRIANGLE
        // -----------------------------------------------------------------------------------
        private void ShowTriangleShape()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            double radius = 150 * scaleFactor; // distance du centre
            double winSize = 80 * scaleFactor;

            for (int i = 0; i < 3; i++)
            {
                double angle = 2.0 * Math.PI * i / 3; // 3 points
                double x = centerX + radius * Math.Cos(angle);
                double y = centerY + radius * Math.Sin(angle);

                TrollWindow tw = new TrollWindow(
                    "Simple mais solide !",
                    System.Windows.Media.Brushes.LightYellow,
                    System.Windows.Media.Brushes.Gold,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = x - (winSize / 2);
                tw.Top = y - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 8) VAGUE (y = A sin(kx))
        // -----------------------------------------------------------------------------------
        private void ShowWaveShape()
        {
            int pointCount = 30;
            double amplitude = 100 * scaleFactor;
            double waveLength = 50 * scaleFactor;
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerY = screenHeight / 2;

            // On laisse un offset plus grand sur les côtés
            double xStart = 200 * scaleFactor;
            double xEnd = screenWidth - 200 * scaleFactor;

            double step = (xEnd - xStart) / (pointCount - 1);
            double winSize = 60 * scaleFactor;

            for (int i = 0; i < pointCount; i++)
            {
                double x = xStart + i * step;
                double y = centerY + amplitude * Math.Sin((2.0 * Math.PI / waveLength) * x);

                TrollWindow tw = new TrollWindow(
                    "Tout en douceur !",
                    System.Windows.Media.Brushes.LightCyan,
                    System.Windows.Media.Brushes.DarkCyan,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = x - (winSize / 2);
                tw.Top = y - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 9) CARRÉ (4 sommets)
        // -----------------------------------------------------------------------------------
        private void ShowSquareShape()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            double halfSide = 100 * scaleFactor;
            double winSize = 80 * scaleFactor;

            double[,] points = new double[,]
            {
                { centerX - halfSide, centerY - halfSide },
                { centerX + halfSide, centerY - halfSide },
                { centerX + halfSide, centerY + halfSide },
                { centerX - halfSide, centerY + halfSide }
            };

            for (int i = 0; i < 4; i++)
            {
                double px = points[i, 0];
                double py = points[i, 1];

                TrollWindow tw = new TrollWindow(
                    "Stable et fiable !",
                    System.Windows.Media.Brushes.LightGray,
                    System.Windows.Media.Brushes.Gray,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = px - (winSize / 2);
                tw.Top = py - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // 10) SYMBOLE « INFINI »
        // -----------------------------------------------------------------------------------
        private void ShowInfinityShape()
        {
            int pointCount = 40;
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double centerX = screenWidth / 2;
            double centerY = screenHeight / 2;

            // Échelle pour agrandir la forme
            double shapeScale = 200 * scaleFactor;
            double winSize = 60 * scaleFactor;

            for (int i = 0; i < pointCount; i++)
            {
                double t = 2.0 * Math.PI * i / pointCount;

                double sinT = Math.Sin(t);
                double cosT = Math.Cos(t);
                double denom = (1 + sinT * sinT);

                double xVal = (cosT / denom) * shapeScale;
                double yVal = (cosT * sinT / denom) * shapeScale;

                double finalX = centerX + xVal;
                double finalY = centerY + yVal;

                TrollWindow tw = new TrollWindow(
                    "Sans limites !",
                    System.Windows.Media.Brushes.White,
                    System.Windows.Media.Brushes.Black,
                    width: winSize,
                    height: winSize
                );
                tw.WindowStartupLocation = WindowStartupLocation.Manual;
                tw.Left = finalX - (winSize / 2);
                tw.Top = finalY - (winSize / 2);
                tw.Show();

                CloseAfterDelay(tw, 2000);
            }
        }

        // -----------------------------------------------------------------------------------
        // Fermeture automatique de la fenêtre après un délai
        // -----------------------------------------------------------------------------------
        private async void CloseAfterDelay(Window w, int ms)
        {
            await Task.Delay(ms);
            if (w != null && w.IsLoaded)
            {
                w.Close();
            }
        }
    }
}
