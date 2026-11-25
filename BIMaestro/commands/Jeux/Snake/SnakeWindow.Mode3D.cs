using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using BIMaestro.Localization;

namespace BIMaestro.Bonus
{
    // Mode 3D : l'écran d'attente montre le plateau en 2D ; au démarrage (Espace),
    // la caméra quitte la vue de dessus pour se placer derrière la tête,
    // un compte à rebours s'affiche, et on joue en vue troisième personne
    // avec des commandes relatives (gauche / droite = tourner).
    // Les règles restent celles de SnakeWindow.xaml.cs : ce fichier ne fait que l'affichage 3D.
    //
    // Performance (le jeu tourne sur le fil d'interface de Revit) :
    // - seul le serpent est reconstruit à chaque image ;
    // - fruits et murets sont des modèles persistants : on ne change que leur position ou leur matériau ;
    // - la mini-carte 2D est rafraîchie 10 fois par seconde, pas à chaque image.
    public partial class SnakeWindow
    {
        private const double Transition3DSec = 1.8;
        private const double CountdownStepSec = 0.75;
        private const int MaxQueuedTurns = 3;
        private const double MiniMapRefreshSec = 0.1;

        private enum Phase3D { None, Transition, Countdown, Active }

        private Phase3D _phase3D = Phase3D.None;
        private double _phase3DTime;
        private int _countdownShown = -1;

        private readonly Queue<Direction> _turnQueue = new Queue<Direction>();

        // Réactivité : la tête part vers la case suivante dès l'appui ; ce décalage lisse le changement.
        private Vector3D _headCorrection;
        private Direction _lastPendingDir;
        private Cell _lastHeadCell;
        private bool _hasLastPending;

        // Scène
        private PerspectiveCamera _camera3D;
        private Model3DGroup _staticGroup3D;
        private Model3DGroup _borderGroup3D;
        private Model3DGroup _fruitGroup3D;
        private Model3DGroup _snakeGroup3D;
        private Model3DGroup _fxGroup3D;
        private Model3DGroup _fruitShadowGroup3D;
        private Model3DGroup _snakeShadowGroup3D;
        private int _static3DCols = -1;
        private int _static3DRows = -1;
        private SkinId? _static3DSkin;
        private int _static3DWallCount = -1;
        private readonly Dictionary<Color, Material> _materials3D = new Dictionary<Color, Material>();
        private readonly Dictionary<Color, Material> _glowMaterials3D = new Dictionary<Color, Material>();
        private readonly Dictionary<Color, Material> _flatMaterials3D = new Dictionary<Color, Material>();
        private readonly Dictionary<double, MeshGeometry3D> _unitSpheres3D = new Dictionary<double, MeshGeometry3D>();

        // Maillages réutilisés d'une image à l'autre (serpent, ombres, effets) : on remplit les mêmes
        // collections au lieu d'en recréer, ce qui évite les pauses du ramasse-miettes dans Revit.
        private LiveMesh3D _bodyA3D, _bodyB3D, _bridges3D, _head3D, _eyes3D, _pupils3D, _snakeShadow3D, _arrow3D;
        private readonly Dictionary<Color, LiveMesh3D> _particleMeshes3D = new Dictionary<Color, LiveMesh3D>();

        // Murets du bord (persistants, seul le matériau change)
        private readonly GeometryModel3D[] _borderModels = new GeometryModel3D[4];
        private readonly double[] _borderLevels = { -1, -1, -1, -1 };

        // Fruits (persistants tant que la liste ne change pas)
        private sealed class Fruit3D
        {
            public TranslateTransform3D Transform;
            public double Phase;
        }
        private readonly List<Fruit3D> _fruitModels3D = new List<Fruit3D>();
        private int _fruitSignature3D = int.MinValue;

        // Caméra (lissée)
        private double _camYaw;
        private double _camFovBoost;
        private double _camShake;

        // Caméras au choix (touche C)
        private enum CameraPreset3D { Close, High }
        private const double CameraBlendSec = 0.6;
        private CameraPreset3D _camPreset = CameraPreset3D.Close;
        private CameraPreset3D _camPresetPrev = CameraPreset3D.Close;
        private double _camPresetBlend = 1.0;

        // Éclairage teinté par le skin
        private static readonly Color AmbientBase3D = Color.FromRgb(0x5A, 0x5E, 0x66);
        private static readonly Color KeyBase3D = Color.FromRgb(0xE8, 0xE6, 0xE0);
        private static readonly Color FillBase3D = Color.FromRgb(0x3A, 0x44, 0x55);
        private AmbientLight _ambient3D;
        private DirectionalLight _keyLight3D;
        private DirectionalLight _fillLight3D;

        // Effets
        private const int MaxParticles3D = 140;
        private readonly List<Particle3D> _particles = new List<Particle3D>();
        private Material _shadowMaterial3D;
        private static readonly Color Danger3DColor = Color.FromRgb(0xFF, 0x3B, 0x3B);
        private static readonly Color Accent3DColor = Color.FromRgb(0x2E, 0xE0, 0x84);
        private static readonly Vector3D ShadowOffset3D = new Vector3D(-0.12, 0, -0.10); // à l'opposé de la lumière principale

        // Mini-carte
        private double _lastMiniMapRefresh = -1;

        private sealed class Particle3D
        {
            public Point3D P;
            public Vector3D V;
            public double Life;
            public double MaxLife;
            public double Size;
            public Color Color;
        }

        private bool Is3DLogicFrozen => _phase3D == Phase3D.Transition || _phase3D == Phase3D.Countdown;

        // -------------------------------
        // Accroches depuis la logique
        // -------------------------------
        private void Reset3D()
        {
            _phase3D = Phase3D.None;
            _phase3DTime = 0;
            _countdownShown = -1;
            _turnQueue.Clear();
            _camFovBoost = 0;
            _camShake = 0;
            _particles.Clear();
            _headCorrection = new Vector3D();
            _hasLastPending = false;

            if (Scene3DHost == null) return;

            MiniMapBorder.Visibility = Visibility.Collapsed;
            Flash3D.BeginAnimation(UIElement.OpacityProperty, null);
            Flash3D.Opacity = 0;
            Flash3D.Visibility = Visibility.Collapsed;
            Scene3DHost.Visibility = Visibility.Collapsed;
            GameViewbox.Visibility = Visibility.Visible;
            CountdownText.Visibility = Visibility.Collapsed;
            ShortcutsText.Text = UiLanguage.T("Espace = démarrer / restart • Flèches ou ZQSD = bouger • P = pause");
        }

        private void Apply3DQueuedTurn()
        {
            if (_phase3D != Phase3D.Active) return;
            if (_turnQueue.Count > 0)
                _nextDir = _turnQueue.Dequeue();
        }

        private void Start3DIfNeeded()
        {
            if (_mode != GameMode.Mode3D || _phase3D != Phase3D.None || _isGameOver) return;

            Begin3DTransition();
        }

        // Le rendu 2D ne sert plus qu'à la mini-carte en 3D : 10 fois par seconde suffisent.
        private bool ShouldRender2DFrame(double t)
        {
            if (_phase3D == Phase3D.None) return true;
            if (MiniMapBorder.Visibility != Visibility.Visible) return false;
            if (t - _lastMiniMapRefresh < MiniMapRefreshSec) return false;

            _lastMiniMapRefresh = t;
            return true;
        }

        private bool TryHandle3DKey(KeyEventArgs e)
        {
            if (_mode != GameMode.Mode3D || _phase3D == Phase3D.None)
                return false;

            // Pendant la transition et le compte à rebours, on avale les touches de direction.
            if (_phase3D != Phase3D.Active)
            {
                e.Handled = true;
                return true;
            }

            Direction? wanted = null;
            var basis = _turnQueue.Count > 0 ? _turnQueue.Last() : _dir;

            if (e.Key == Key.Left || e.Key == Key.Q) wanted = TurnLeft(basis);
            else if (e.Key == Key.Right || e.Key == Key.D) wanted = TurnRight(basis);
            else if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Z || e.Key == Key.S)
            {
                // Haut / bas n'ont pas de sens en vue derrière le serpent.
                e.Handled = true;
                return true;
            }
            else return false;

            // Chaque appui compte : deux flèches rapides = deux virages (demi-tour en deux temps).
            if (_turnQueue.Count < MaxQueuedTurns)
                _turnQueue.Enqueue(wanted.Value);

            e.Handled = true;
            return true;
        }

        private static Direction TurnLeft(Direction d)
        {
            switch (d)
            {
                case Direction.Right: return Direction.Up;
                case Direction.Up: return Direction.Left;
                case Direction.Left: return Direction.Down;
                default: return Direction.Right;
            }
        }

        private static Direction TurnRight(Direction d)
        {
            switch (d)
            {
                case Direction.Right: return Direction.Down;
                case Direction.Down: return Direction.Left;
                case Direction.Left: return Direction.Up;
                default: return Direction.Right;
            }
        }

        // -------------------------------
        // Transition
        // -------------------------------
        private void Begin3DTransition()
        {
            _phase3D = Phase3D.Transition;
            _phase3DTime = 0;
            _countdownShown = -1;
            _accumMs = 0;
            _turnQueue.Clear();
            _nextDir = _dir;
            _headCorrection = new Vector3D();
            _hasLastPending = false;

            Ensure3DScene();
            _camYaw = YawOf(_dir);
            _camFovBoost = 0;

            Scene3DHost.Visibility = Visibility.Visible;
            GameViewbox.Visibility = Visibility.Collapsed;

            ShowToast(UiLanguage.T("Passage en 3D !", "Switching to 3D!"), TimeSpan.FromSeconds(1.6));
        }

        private void Update3D(double dt, double t)
        {
            if (_phase3D == Phase3D.None) return;

            if (dt < 0) dt = 0;
            if (dt > 0.1) dt = 0.1;

            if (_phase3D == Phase3D.Transition)
            {
                _phase3DTime += dt;
                if (_phase3DTime >= Transition3DSec)
                {
                    _phase3D = Phase3D.Countdown;
                    _phase3DTime = 0;
                    UpdateShortcuts3D();
                }
            }
            else if (_phase3D == Phase3D.Countdown)
            {
                _phase3DTime += dt;
                int step = (int)(_phase3DTime / CountdownStepSec);
                if (step >= 3)
                {
                    _phase3D = Phase3D.Active;
                    _phase3DTime = 0;
                    _accumMs = 0;
                    CountdownText.Visibility = Visibility.Collapsed;
                }
                else if (step != _countdownShown)
                {
                    _countdownShown = step;
                    ShowCountdown((3 - step).ToString());
                }
            }

            if (!_isPaused)
                UpdateParticles3D(dt);

            UpdateMiniMap3D();
            Ensure3DScene();

            var snake = _snake.ToArray();
            if (snake.Length == 0) return;

            var pts = ComputeSnakePoints(snake, dt);

            UpdateFruits3D(t);
            UpdateBorderWalls3D(snake[0], t);
            BuildSnake3D(snake, pts);
            BuildEffects3D(pts[0]);
            UpdateCamera3D(dt, pts[0]);
        }

        private void UpdateMiniMap3D()
        {
            bool show = _phase3D == Phase3D.Active || _phase3D == Phase3D.Countdown;
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (MiniMapBorder.Visibility != v) MiniMapBorder.Visibility = v;
            if (show && !ReferenceEquals(MiniMapImage.Source, _wb))
                MiniMapImage.Source = _wb;
        }

        // -------------------------------
        // Effets quand on mange
        // -------------------------------
        private void On3DFruitEaten(Fruit fruit)
        {
            if (_phase3D == Phase3D.None || _activeSkin == null) return;

            var color = _activeSkin.GetFruitColor(fruit.Type);
            var light = Lighten(color, 0.45);
            bool bonus = fruit.Type != FruitType.Normal;
            int n = bonus ? 18 : 8;
            var origin = new Point3D(fruit.Pos.X + 0.5, 0.45, fruit.Pos.Y + 0.5);

            for (int i = 0; i < n && _particles.Count < MaxParticles3D; i++)
            {
                double a = _rng.NextDouble() * 2 * Math.PI;
                double speed = (bonus ? 1.6 : 1.1) + _rng.NextDouble() * (bonus ? 2.6 : 1.6);
                double life = 0.4 + _rng.NextDouble() * (bonus ? 0.45 : 0.25);
                _particles.Add(new Particle3D
                {
                    P = origin,
                    V = new Vector3D(Math.Cos(a) * speed, 2.0 + _rng.NextDouble() * 2.6, Math.Sin(a) * speed),
                    Life = life,
                    MaxLife = life,
                    Size = 0.08 + _rng.NextDouble() * 0.08,
                    Color = i % 3 == 0 ? light : color
                });
            }

            if (bonus)
            {
                _camShake = Math.Max(_camShake, 0.2);
                Flash3DColor(color, fruit.Type == FruitType.Multiplier ? 0.38 : 0.24);
            }
            else
            {
                _camShake = Math.Max(_camShake, 0.05);
            }
        }

        private void Flash3DColor(Color color, double strength)
        {
            Flash3D.Background = new SolidColorBrush(color);
            Flash3D.Visibility = Visibility.Visible;

            var anim = new DoubleAnimation(strength, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            // Une couche plein écran, même transparente, coûte à chaque image : on la retire après l'éclair.
            anim.Completed += (s, e) => Flash3D.Visibility = Visibility.Collapsed;
            Flash3D.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void UpdateParticles3D(double dt)
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life -= dt;
                if (p.Life <= 0) { _particles.RemoveAt(i); continue; }

                p.V = new Vector3D(p.V.X, p.V.Y - 9.0 * dt, p.V.Z);
                p.P += p.V * dt;

                if (p.P.Y < 0.05)
                {
                    p.P = new Point3D(p.P.X, 0.05, p.P.Z);
                    p.V = new Vector3D(p.V.X * 0.6, -p.V.Y * 0.35, p.V.Z * 0.6);
                }
            }
        }

        private void ShowCountdown(string text)
        {
            CountdownText.Text = text;
            CountdownText.Visibility = Visibility.Visible;

            var dur = TimeSpan.FromMilliseconds(CountdownStepSec * 1000 * 0.9);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.6, 1.0, dur) { EasingFunction = ease });
            CountdownScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.6, 1.0, dur) { EasingFunction = ease });
            CountdownText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.15, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }

        // -------------------------------
        // Caméra
        // -------------------------------
        private bool TryCycleCamera3D()
        {
            if (_mode != GameMode.Mode3D || _phase3D == Phase3D.None || _phase3D == Phase3D.Transition)
                return false;

            _camPresetPrev = _camPreset;
            _camPreset = _camPreset == CameraPreset3D.Close ? CameraPreset3D.High : CameraPreset3D.Close;
            _camPresetBlend = 0;
            _turnQueue.Clear();

            UpdateShortcuts3D();
            ShowToast("🎥 " + UiLanguage.T("Caméra : ", "Camera: ") + CameraPresetName(_camPreset), TimeSpan.FromSeconds(1.2));
            SaveStateSafe();
            return true;
        }

        private static string CameraPresetName(CameraPreset3D preset)
        {
            switch (preset)
            {
                case CameraPreset3D.High: return UiLanguage.T("Haute", "High");
                default: return UiLanguage.T("Proche", "Close");
            }
        }

        private void UpdateShortcuts3D()
        {
            ShortcutsText.Text = UiLanguage.T(
                "← / Q = tourner à gauche • → / D = tourner à droite • C = caméra • P = pause",
                "← / Q = turn left • → / D = turn right • C = camera • P = pause");
        }

        private void ComputeCameraPreset(CameraPreset3D preset, Point3D head, Vector3D fwd, out Point3D pos, out Point3D target, out double fov)
        {
            switch (preset)
            {
                case CameraPreset3D.High:
                    // Plus haute et plus loin : moins de mouvement à l'écran, moins de mal de mer.
                    pos = head - fwd * 7.5 + new Vector3D(0, 6.8, 0);
                    target = head + fwd * 4.0;
                    fov = 58;
                    return;

                default:
                    pos = head - fwd * 4.6 + new Vector3D(0, 3.3, 0);
                    target = head + fwd * 3.0 + new Vector3D(0, 0.2, 0);
                    fov = 64;
                    return;
            }
        }

        private static double YawOf(Direction d)
        {
            // Avant = (cos a, 0, sin a) ; la grille Y vers le bas correspond à +Z.
            switch (d)
            {
                case Direction.Right: return 0;
                case Direction.Down: return Math.PI / 2;
                case Direction.Left: return Math.PI;
                default: return -Math.PI / 2;
            }
        }

        private static double WrapAngle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private void UpdateCamera3D(double dt, Point3D head)
        {
            if (_camera3D == null) return;

            // La caméra pivote vers la direction demandée dès l'appui, en douceur.
            double targetYaw = YawOf(PendingDirection());
            double k = 1 - Math.Exp(-dt * 8.0);
            _camYaw = WrapAngle(_camYaw + WrapAngle(targetYaw - _camYaw) * k);

            double boostTarget = (_speedActive ? 8.0 : 0.0);
            _camFovBoost += (boostTarget - _camFovBoost) * (1 - Math.Exp(-dt * 4.0));

            var fwd = new Vector3D(Math.Cos(_camYaw), 0, Math.Sin(_camYaw));

            // Caméra choisie, avec un fondu quand on change de caméra (touche C).
            _camPresetBlend = Math.Min(1.0, _camPresetBlend + dt / CameraBlendSec);
            ComputeCameraPreset(_camPreset, head, fwd, out var chasePos, out var chaseTarget, out double chaseFov);
            if (_camPresetBlend < 1.0)
            {
                ComputeCameraPreset(_camPresetPrev, head, fwd, out var prevPos, out var prevTarget, out double prevFov);
                double b = _camPresetBlend * _camPresetBlend * (3 - 2 * _camPresetBlend);
                chasePos = Lerp(prevPos, chasePos, b);
                chaseTarget = Lerp(prevTarget, chaseTarget, b);
                chaseFov = prevFov + (chaseFov - prevFov) * b;
            }
            chaseFov += _camFovBoost;
            var chaseUp = new Vector3D(0, 1, 0);

            double cx = _cols / 2.0, cz = _rows / 2.0;
            const double topFov = 45;
            double topHeight = (Math.Max(_cols, _rows) / 2.0 + 0.8) / Math.Tan(topFov / 2 * Math.PI / 180);
            var topPos = new Point3D(cx, topHeight, cz);
            var topTarget = new Point3D(cx, 0, cz);
            var topUp = new Vector3D(0, 0, -1);

            double e = 1;
            if (_phase3D == Phase3D.Transition)
            {
                double p = Clamp01(_phase3DTime / Transition3DSec);
                e = p * p * (3 - 2 * p); // smoothstep
                e = e * e * (3 - 2 * e); // départ et arrivée encore plus doux
            }

            var pos = Lerp(topPos, chasePos, e);
            var target = Lerp(topTarget, chaseTarget, e);
            var up = topUp + (chaseUp - topUp) * e;
            up.Normalize();

            // Secousse brève quand on mange un bonus.
            _camShake *= Math.Exp(-dt * 7.0);
            if (_camShake < 0.002) _camShake = 0;
            var shake = new Vector3D();
            if (_camShake > 0 && !_isPaused)
                shake = new Vector3D((_rng.NextDouble() - 0.5) * 2 * _camShake,
                                     (_rng.NextDouble() - 0.5) * 1.2 * _camShake,
                                     (_rng.NextDouble() - 0.5) * 2 * _camShake);

            _camera3D.Position = pos + shake;
            _camera3D.LookDirection = target - pos; // la cible ne bouge pas : la secousse reste une translation
            _camera3D.UpDirection = up;
            _camera3D.FieldOfView = topFov + (chaseFov - topFov) * e;
        }

        private static Point3D Lerp(Point3D a, Point3D b, double t)
            => new Point3D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        // -------------------------------
        // Position affichée du serpent
        // -------------------------------

        // Direction que prendra le serpent au prochain pas (premier virage en attente).
        private Direction PendingDirection()
        {
            if (_phase3D == Phase3D.Active && _turnQueue.Count > 0)
                return _turnQueue.Peek();
            return _nextDir;
        }

        // Fraction du pas en cours (0 = case actuelle, 1 = case suivante).
        private double StepProgress()
        {
            if (_phase3D != Phase3D.Active || _isGameOver || _isWaitingStart)
                return 0;
            return Clamp01(_accumMs / GetCurrentStepMs());
        }

        private static Cell StepCell(Cell c, Direction d)
        {
            switch (d)
            {
                case Direction.Up: return new Cell(c.X, c.Y - 1);
                case Direction.Down: return new Cell(c.X, c.Y + 1);
                case Direction.Left: return new Cell(c.X - 1, c.Y);
                default: return new Cell(c.X + 1, c.Y);
            }
        }

        private static Point3D CellCenter(Cell c) => new Point3D(c.X + 0.5, 0, c.Y + 0.5);

        // On dessine le serpent entre sa case actuelle et la case SUIVANTE (et non la précédente) :
        // un virage se voit donc dès l'appui, au lieu d'un pas plus tard.
        private Point3D[] ComputeSnakePoints(Cell[] snake, double dt)
        {
            int n = snake.Length;
            double alpha = StepProgress();
            var pending = PendingDirection();

            var head = snake[0];
            var nextHead = StepCell(head, pending);

            bool blocked = nextHead.X < 0 || nextHead.X >= _cols || nextHead.Y < 0 || nextHead.Y >= _rows
                           || _walls.Contains(nextHead);
            bool willEat = false;
            if (!blocked)
            {
                for (int i = 0; i < _fruits.Count; i++)
                    if (_fruits[i].Pos.Equals(nextHead)) { willEat = true; break; }

                int last = willEat ? n : n - 1; // la queue libère sa case si on ne mange pas
                for (int i = 0; i < last; i++)
                    if (snake[i].Equals(nextHead)) { blocked = true; break; }
            }
            if (blocked) alpha = 0; // pas d'avancée dans un mur ou dans le corps

            var pts = new Point3D[n];
            for (int i = 0; i < n; i++)
            {
                var from = CellCenter(snake[i]);
                Point3D to;
                if (i == 0) to = CellCenter(nextHead);
                else if (i == n - 1 && willEat) to = from;
                else to = CellCenter(snake[i - 1]);
                pts[i] = Lerp(from, to, alpha);
            }

            // Changement de direction en cours de pas : la tête change de cible d'un coup ;
            // on absorbe ce saut en quelques centièmes de seconde pour qu'il reste fluide.
            if (_hasLastPending && _phase3D == Phase3D.Active && pending != _lastPendingDir && head.Equals(_lastHeadCell))
            {
                var oldTarget = Lerp(CellCenter(head), CellCenter(StepCell(head, _lastPendingDir)), alpha);
                _headCorrection += oldTarget - pts[0];
            }
            _headCorrection *= Math.Exp(-dt * 22.0);
            if (_headCorrection.Length < 0.002) _headCorrection = new Vector3D();
            pts[0] += _headCorrection;

            _lastPendingDir = pending;
            _lastHeadCell = head;
            _hasLastPending = true;

            return pts;
        }

        // -------------------------------
        // Scène statique (sol, décor, murs)
        // -------------------------------
        private void Ensure3DScene()
        {
            if (_camera3D == null)
            {
                _camera3D = new PerspectiveCamera { NearPlaneDistance = 0.1, FarPlaneDistance = 400 };
                Scene3DViewport.Camera = _camera3D;

                _ambient3D = new AmbientLight(AmbientBase3D);
                _keyLight3D = new DirectionalLight(KeyBase3D, new Vector3D(-0.45, -1.0, -0.35));
                _fillLight3D = new DirectionalLight(FillBase3D, new Vector3D(0.6, -0.5, 0.7));

                var lights = new Model3DGroup();
                lights.Children.Add(_ambient3D);
                lights.Children.Add(_keyLight3D);
                lights.Children.Add(_fillLight3D);

                _staticGroup3D = new Model3DGroup();
                _borderGroup3D = new Model3DGroup();
                _fruitGroup3D = new Model3DGroup();
                _snakeGroup3D = new Model3DGroup();
                _fxGroup3D = new Model3DGroup();
                _fruitShadowGroup3D = new Model3DGroup();
                _snakeShadowGroup3D = new Model3DGroup();

                _bodyA3D = new LiveMesh3D(); _bodyB3D = new LiveMesh3D(); _bridges3D = new LiveMesh3D();
                _head3D = new LiveMesh3D(); _eyes3D = new LiveMesh3D(); _pupils3D = new LiveMesh3D();
                _snakeShadow3D = new LiveMesh3D(); _arrow3D = new LiveMesh3D();
                foreach (var lm in new[] { _bodyA3D, _bodyB3D, _bridges3D, _head3D, _eyes3D, _pupils3D })
                    _snakeGroup3D.Children.Add(lm.Model);
                _snakeShadowGroup3D.Children.Add(_snakeShadow3D.Model);
                _fxGroup3D.Children.Add(_arrow3D.Model);

                // Les ombres (transparentes) sont dessinées en dernier.
                foreach (var g3 in new Model3D[] { lights, _staticGroup3D, _borderGroup3D, _fruitGroup3D, _snakeGroup3D, _fxGroup3D, _fruitShadowGroup3D, _snakeShadowGroup3D })
                    Scene3DViewport.Children.Add(new ModelVisual3D { Content = g3 });

                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sky.GradientStops.Add(new GradientStop(Colors.Black, 0));
                sky.GradientStops.Add(new GradientStop(Colors.Black, 0.48));
                sky.GradientStops.Add(new GradientStop(Colors.Black, 1));
                Scene3DHost.Background = sky;

                var g = PlayfieldBorder.RenderTransform as TransformGroup;
                if (g != null)
                {
                    Scene3DHost.RenderTransform = g;
                    Scene3DHost.RenderTransformOrigin = new Point(0.5, 0.5);
                }
            }

            if (_activeSkin == null) return;

            if (_static3DCols == _cols && _static3DRows == _rows && _static3DSkin == _activeSkin.Id && _static3DWallCount == _walls.Count)
                return;

            _static3DCols = _cols;
            _static3DRows = _rows;
            _static3DSkin = _activeSkin.Id;
            _static3DWallCount = _walls.Count;

            // Décor selon le skin : ciel, horizon brumeux, lumière teintée.
            var horizon = Horizon3DColor(_activeSkin);
            if (Scene3DHost.Background is LinearGradientBrush skyBrush)
            {
                skyBrush.GradientStops[0].Color = Shade(_activeSkin.BackgroundColor, 0.8);
                skyBrush.GradientStops[1].Color = horizon;
                skyBrush.GradientStops[2].Color = horizon;
            }

            _ambient3D.Color = LerpColor(AmbientBase3D, _activeSkin.HeadColor, 0.12);
            _keyLight3D.Color = LerpColor(KeyBase3D, _activeSkin.HeadColor, 0.10);
            _fillLight3D.Color = LerpColor(FillBase3D, _activeSkin.HeadColor, 0.45);

            _staticGroup3D.Children.Clear();

            BuildDecor3D(horizon);

            var bg = _activeSkin.BackgroundColor;
            var tileA = new MeshBuilder3D();
            var tileB = new MeshBuilder3D();
            for (int y = 0; y < _rows; y++)
                for (int x = 0; x < _cols; x++)
                    ((x + y) % 2 == 0 ? tileA : tileB).AddQuadUp(x + 0.03, y + 0.03, x + 0.97, y + 0.97, 0);

            var under = new MeshBuilder3D();
            under.AddBox(new Point3D(_cols / 2.0, -0.06, _rows / 2.0), new Vector3D(_cols + 0.4, 0.1, _rows + 0.4));

            _staticGroup3D.Children.Add(under.ToModel(Material3D(Lighten(bg, 0.04))));
            _staticGroup3D.Children.Add(tileA.ToModel(Material3D(LerpColor(bg, _activeSkin.GridColor, 0.10))));
            _staticGroup3D.Children.Add(tileB.ToModel(Material3D(LerpColor(bg, _activeSkin.GridColor, 0.16))));

            if (_walls.Count > 0)
            {
                var walls = new MeshBuilder3D();
                foreach (var c in _walls)
                    walls.AddBox(new Point3D(c.X + 0.5, 0.45, c.Y + 0.5), new Vector3D(0.96, 0.9, 0.96));
                _staticGroup3D.Children.Add(walls.ToModel(Material3D(Lighten(_activeSkin.WallColor, 0.25))));
            }

            BuildBorderWalls3D();

            // Skin ou carte changés : les fruits seront recréés avec les bonnes couleurs.
            _fruitSignature3D = int.MinValue;
        }

        private static Color Horizon3DColor(SkinDefinition skin)
            => Lighten(LerpColor(skin.BackgroundColor, skin.HeadColor, 0.16), 0.10);

        private void BuildDecor3D(Color horizon)
        {
            double cx = _cols / 2.0, cz = _rows / 2.0;
            double half = Math.Max(_cols, _rows) / 2.0;
            var nearGround = Lighten(_activeSkin.BackgroundColor, 0.05);

            // Sol qui s'étend jusqu'à l'horizon, en anneaux de plus en plus proches de la couleur
            // du ciel : sans brouillard natif en WPF 3D, c'est ce qui donne l'effet de brume.
            double[] radii = { 0, half + 4, half + 12, half + 26, half + 48, half + 80, 150 };
            for (int i = 0; i < radii.Length - 1; i++)
            {
                double f = (i + 1) / (double)(radii.Length - 1);
                var band = new MeshBuilder3D();
                AddGroundFrame(band, cx, cz, radii[i], radii[i + 1], -0.14);
                _staticGroup3D.Children.Add(band.ToModel(FlatMaterial3D(LerpColor(nearGround, horizon, f * f * 0.85 + f * 0.15))));
            }

            // Silhouettes au loin (blocs), teintées vers l'horizon selon la distance.
            var rnd = new Random(((int)_activeSkin.Id + 1) * 7919 + _cols);
            var stone = LerpColor(_activeSkin.WallColor, _activeSkin.GridColor, 0.12);
            var byFog = new Dictionary<int, MeshBuilder3D>();
            int placed = 0;
            for (int tries = 0; tries < 400 && placed < 40; tries++)
            {
                double ang = rnd.NextDouble() * 2 * Math.PI;
                double dist = half + 6 + Math.Pow(rnd.NextDouble(), 0.7) * 48;
                double x = cx + Math.Cos(ang) * dist;
                double z = cz + Math.Sin(ang) * dist;

                // Jamais sur le plateau ni juste à côté (la caméra proche passe au-dessus du bord).
                if (Math.Abs(x - cx) < half + 6 && Math.Abs(z - cz) < half + 6)
                    continue;

                double w = 0.8 + rnd.NextDouble() * 2.2;
                double h = 0.6 + rnd.NextDouble() * rnd.NextDouble() * 9.0;
                double d = w * (0.6 + rnd.NextDouble() * 0.8);

                int fog = (int)Math.Round(Clamp01((dist - half) / 55.0) * 4);
                if (!byFog.TryGetValue(fog, out var mb)) byFog[fog] = mb = new MeshBuilder3D();
                mb.AddBox(new Point3D(x, h / 2 - 0.14, z), new Vector3D(w, h, d));
                placed++;
            }

            foreach (var kv in byFog)
                _staticGroup3D.Children.Add(kv.Value.ToModel(Material3D(LerpColor(stone, horizon, kv.Key / 4.0 * 0.9))));
        }

        private static void AddGroundFrame(MeshBuilder3D mb, double cx, double cz, double inner, double outer, double y)
        {
            if (inner <= 0)
            {
                mb.AddQuadUp(cx - outer, cz - outer, cx + outer, cz + outer, y);
                return;
            }

            mb.AddQuadUp(cx - outer, cz - outer, cx + outer, cz - inner, y);
            mb.AddQuadUp(cx - outer, cz + inner, cx + outer, cz + outer, y);
            mb.AddQuadUp(cx - outer, cz - inner, cx - inner, cz + inner, y);
            mb.AddQuadUp(cx + inner, cz - inner, cx + outer, cz + inner, y);
        }

        // -------------------------------
        // Murets du bord : rouges quand la tête s'approche (moins de 2 cases)
        // -------------------------------
        private void BuildBorderWalls3D()
        {
            _borderGroup3D.Children.Clear();

            const double wh = 0.55, th = 0.3;
            var boxes = new[]
            {
                (new Point3D(_cols / 2.0, wh / 2, -th / 2), new Vector3D(_cols + 2 * th, wh, th)),          // haut
                (new Point3D(_cols / 2.0, wh / 2, _rows + th / 2), new Vector3D(_cols + 2 * th, wh, th)),   // bas
                (new Point3D(-th / 2, wh / 2, _rows / 2.0), new Vector3D(th, wh, _rows)),                   // gauche
                (new Point3D(_cols + th / 2, wh / 2, _rows / 2.0), new Vector3D(th, wh, _rows)),            // droite
            };

            for (int i = 0; i < 4; i++)
            {
                var mb = new MeshBuilder3D();
                mb.AddBox(boxes[i].Item1, boxes[i].Item2);
                var model = mb.ToModel(Material3D(BorderBaseColor()), freezeModel: false);
                _borderModels[i] = model;
                _borderLevels[i] = -1;
                _borderGroup3D.Children.Add(model);
            }
        }

        private Color BorderBaseColor() => LerpColor(_activeSkin.WallColor, _activeSkin.HeadColor, 0.35);

        private void UpdateBorderWalls3D(Cell head, double t)
        {
            if (_borderModels[0] == null) return;

            double pulse = 0.75 + 0.25 * Math.Sin(2 * Math.PI * 3.0 * t);
            int[] dist = { head.Y, _rows - 1 - head.Y, head.X, _cols - 1 - head.X };

            for (int i = 0; i < 4; i++)
            {
                double level = (_isWaitingStart || _phase3D != Phase3D.Active) ? 0
                             : dist[i] <= 0 ? 1.0 : dist[i] == 1 ? 0.55 : 0.0;

                // Intensité arrondie au dixième : on ne touche au matériau que s'il change.
                double k = Math.Round(level * pulse * 10) / 10.0;
                if (k == _borderLevels[i]) continue;
                _borderLevels[i] = k;

                _borderModels[i].Material = k <= 0
                    ? Material3D(BorderBaseColor())
                    : GlowMaterial3D(LerpColor(BorderBaseColor(), Danger3DColor, k));
            }
        }

        // -------------------------------
        // Fruits : recréés seulement quand la liste change, sinon on déplace juste leur hauteur
        // -------------------------------
        private void UpdateFruits3D(double t)
        {
            int sig = 17;
            foreach (var f in _fruits)
                sig = unchecked(sig * 31 + (f.Pos.X * 397 ^ f.Pos.Y) * 7 + (int)f.Type);

            if (sig != _fruitSignature3D)
            {
                _fruitSignature3D = sig;
                _fruitGroup3D.Children.Clear();
                _fruitShadowGroup3D.Children.Clear();
                _fruitModels3D.Clear();

                var shadows = new MeshBuilder3D();
                var beams = new Dictionary<FruitType, MeshBuilder3D>();

                foreach (var f in _fruits)
                {
                    double r = f.Type == FruitType.Normal ? 0.3 : 0.34;
                    var c = new Point3D(f.Pos.X + 0.5, 0.45, f.Pos.Y + 0.5);

                    var transform = new TranslateTransform3D(c.X, c.Y, c.Z);
                    var sphere = new GeometryModel3D(UnitSphere3D(r), GlowMaterial3D(_activeSkin.GetFruitColor(f.Type))) { Transform = transform };
                    _fruitGroup3D.Children.Add(sphere);
                    _fruitModels3D.Add(new Fruit3D { Transform = transform, Phase = f.Pos.X * 0.7 + f.Pos.Y * 0.4 });

                    shadows.AddDiscUp(c + ShadowOffset3D * 0.6, r * 0.9, 0.012, 16);

                    if (f.Type != FruitType.Normal)
                    {
                        if (!beams.TryGetValue(f.Type, out var bm)) beams[f.Type] = bm = new MeshBuilder3D();
                        bm.AddBox(new Point3D(c.X, 1.6, c.Z), new Vector3D(0.07, 2.2, 0.07));
                    }
                }

                foreach (var kv in beams)
                    _fruitGroup3D.Children.Add(kv.Value.ToModel(GlowMaterial3D(_activeSkin.GetFruitColor(kv.Key))));
                _fruitShadowGroup3D.Children.Add(shadows.ToModel(ShadowMaterial3D()));
            }

            // Flottement : seule la translation de chaque fruit bouge (aucune géométrie recréée).
            foreach (var fm in _fruitModels3D)
                fm.Transform.OffsetY = 0.45 + 0.08 * Math.Sin(2 * Math.PI * 1.1 * t + fm.Phase);
        }

        private MeshGeometry3D UnitSphere3D(double r)
        {
            if (_unitSpheres3D.TryGetValue(r, out var mesh)) return mesh;

            var mb = new MeshBuilder3D();
            mb.AddSphere(new Point3D(0, 0, 0), r, 14, 10);
            mesh = mb.ToMesh();
            _unitSpheres3D[r] = mesh;
            return mesh;
        }

        // -------------------------------
        // Serpent (seule géométrie reconstruite à chaque image)
        // -------------------------------
        private void BuildSnake3D(Cell[] snake, Point3D[] pts)
        {
            if (_activeSkin == null) return;

            int count = snake.Length;
            var widths = new double[count];
            var heights = new double[count];
            for (int i = 0; i < count; i++)
            {
                // La queue s'affine sur les 6 derniers segments.
                double tail = Clamp01((count - 1 - i) / 6.0);
                widths[i] = 0.42 + 0.42 * tail;
                heights[i] = 0.34 + 0.34 * tail;
            }

            var shadows = _snakeShadow3D.Begin();

            // Corps continu : un bloc par case + un pont entre cases voisines.
            var bodyA = _bodyA3D.Begin();
            var bodyB = _bodyB3D.Begin();
            var bridges = _bridges3D.Begin();
            for (int i = 1; i < count; i++)
            {
                var p = pts[i];
                (i % 2 == 0 ? bodyA : bodyB).AddBox(new Point3D(p.X, heights[i] / 2, p.Z), new Vector3D(widths[i], heights[i], widths[i]));
                AddShadowSquare(shadows, p + ShadowOffset3D, widths[i] + 0.1);
            }
            for (int i = 0; i < count - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                if ((b - a).Length > 1.5) continue; // pas de pont à travers la carte
                double w = Math.Min(widths[i], widths[i + 1]) * 0.86;
                double h = Math.Min(heights[i], heights[i + 1]) * 0.9;
                bridges.AddOrientedBox(a, b, w, h);
            }
            _bodyA3D.End(Material3D(_activeSkin.BodyColor));
            _bodyB3D.End(Material3D(Shade(_activeSkin.BodyColor, 0.82)));
            _bridges3D.End(Material3D(Shade(_activeSkin.BodyColor, 0.9)));

            // Tête + yeux, tournés vers la direction demandée.
            var head = pts[0];
            var fwd = DirVector(PendingDirection());
            var side = new Vector3D(-fwd.Z, 0, fwd.X);

            _head3D.Begin().AddBox(new Point3D(head.X, 0.4, head.Z), new Vector3D(0.94, 0.8, 0.94));
            _head3D.End(Material3D(_activeSkin.HeadColor));
            AddShadowSquare(shadows, head + ShadowOffset3D * 1.3, 1.02);

            var eyes = _eyes3D.Begin();
            var pupils = _pupils3D.Begin();
            for (int s = -1; s <= 1; s += 2)
            {
                var eye = head + fwd * 0.4 + side * (0.24 * s) + new Vector3D(0, 0.62, 0);
                eyes.AddBox(eye, new Vector3D(0.2, 0.2, 0.2));
                pupils.AddBox(eye + fwd * 0.08, new Vector3D(0.1, 0.12, 0.1));
            }
            _eyes3D.End(Material3D(Colors.White));
            _pupils3D.End(Material3D(Color.FromRgb(0x10, 0x10, 0x14)));

            _snakeShadow3D.End(ShadowMaterial3D());
        }

        // Particules + flèche du dernier virage enregistré.
        private void BuildEffects3D(Point3D head)
        {
            var arrow = _arrow3D.Begin();
            if (_phase3D == Phase3D.Active && _turnQueue.Count > 0)
            {
                // La flèche montre le DERNIER virage en file, devant la tête.
                var q = DirVector(_turnQueue.Last());
                var perp = new Vector3D(-q.Z, 0, q.X);
                var center = head + DirVector(PendingDirection()) * 1.0;
                arrow.AddOrientedBox(center - q * 0.32, center + q * 0.08, 0.16, 0.02, 0.015);
                arrow.AddTriangleUp(center + q * 0.48, center + q * 0.04 + perp * 0.3, center + q * 0.04 - perp * 0.3, 0.035);
            }
            _arrow3D.End(GlowMaterial3D(Accent3DColor));

            // Un maillage par couleur de particule, réutilisé ; vide, il ne coûte rien.
            foreach (var lm in _particleMeshes3D.Values) lm.Begin();
            foreach (var p in _particles)
            {
                if (!_particleMeshes3D.TryGetValue(p.Color, out var lm))
                {
                    lm = new LiveMesh3D();
                    _particleMeshes3D[p.Color] = lm;
                    _fxGroup3D.Children.Add(lm.Model);
                    lm.Begin();
                }
                double sz = p.Size * (0.35 + 0.65 * Clamp01(p.Life / p.MaxLife));
                lm.Builder.AddBox(p.P, new Vector3D(sz, sz, sz));
            }
            foreach (var kv in _particleMeshes3D) kv.Value.End(GlowMaterial3D(kv.Key));
        }

        private static Vector3D DirVector(Direction d)
        {
            double a = YawOf(d);
            return new Vector3D(Math.Round(Math.Cos(a)), 0, Math.Round(Math.Sin(a)));
        }

        private static void AddShadowSquare(MeshBuilder3D mb, Point3D c, double size)
        {
            double h = size / 2;
            mb.AddQuadUp(c.X - h, c.Z - h, c.X + h, c.Z + h, 0.01);
        }

        // -------------------------------
        // Matériaux (une seule passe de rendu sauf pour ce qui brille)
        // -------------------------------
        private Material Material3D(Color c)
        {
            if (_materials3D.TryGetValue(c, out var m)) return m;

            var mat = new DiffuseMaterial(new SolidColorBrush(c));
            mat.Freeze();
            _materials3D[c] = mat;
            return mat;
        }

        private Material ShadowMaterial3D()
        {
            if (_shadowMaterial3D != null) return _shadowMaterial3D;
            var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)));
            mat.Freeze();
            _shadowMaterial3D = mat;
            return mat;
        }

        private Material GlowMaterial3D(Color c)
        {
            if (_glowMaterials3D.TryGetValue(c, out var m)) return m;

            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(new SolidColorBrush(c)));
            group.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B))));
            group.Freeze();
            _glowMaterials3D[c] = group;
            return group;
        }

        // Couleur exacte, sans éclairage (sol lointain, brume).
        private Material FlatMaterial3D(Color c)
        {
            if (_flatMaterials3D.TryGetValue(c, out var m)) return m;

            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(new SolidColorBrush(Colors.Black)));
            group.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
            group.Freeze();
            _flatMaterials3D[c] = group;
            return group;
        }

        // -------------------------------
        // Maillage réutilisé : modèle persistant dont on remplit les mêmes collections à chaque image.
        // Les collections sont détachées pendant le remplissage pour ne pas notifier WPF à chaque ajout.
        // -------------------------------
        private sealed class LiveMesh3D
        {
            public readonly GeometryModel3D Model;
            public MeshBuilder3D Builder { get; private set; }
            private readonly MeshGeometry3D _mesh = new MeshGeometry3D();
            private readonly Point3DCollection _p = new Point3DCollection();
            private readonly Vector3DCollection _n = new Vector3DCollection();
            private readonly Int32Collection _i = new Int32Collection();

            public LiveMesh3D()
            {
                Model = new GeometryModel3D(_mesh, null);
            }

            public MeshBuilder3D Begin()
            {
                _mesh.Positions = null;
                _mesh.Normals = null;
                _mesh.TriangleIndices = null;
                _p.Clear(); _n.Clear(); _i.Clear();
                return Builder ?? (Builder = new MeshBuilder3D(_p, _n, _i));
            }

            public void End(Material material)
            {
                _mesh.Positions = _p;
                _mesh.Normals = _n;
                _mesh.TriangleIndices = _i;
                if (!ReferenceEquals(Model.Material, material))
                    Model.Material = material;
            }
        }

        // -------------------------------
        // Construction de maillages
        // -------------------------------
        private sealed class MeshBuilder3D
        {
            private readonly Point3DCollection _positions;
            private readonly Vector3DCollection _normals;
            private readonly Int32Collection _indices;

            // capacity = nombre approximatif de quadrilatères attendus.
            public MeshBuilder3D(int capacity = 8)
            {
                _positions = new Point3DCollection(capacity * 4);
                _normals = new Vector3DCollection(capacity * 4);
                _indices = new Int32Collection(capacity * 6);
            }

            public MeshBuilder3D(Point3DCollection positions, Vector3DCollection normals, Int32Collection indices)
            {
                _positions = positions;
                _normals = normals;
                _indices = indices;
            }

            public void AddQuadUp(double x0, double z0, double x1, double z1, double y)
            {
                AddQuad(new Point3D(x0, y, z0), new Point3D(x0, y, z1), new Point3D(x1, y, z1), new Point3D(x1, y, z0), new Vector3D(0, 1, 0));
            }

            // Boîte sans face du dessous : la caméra est toujours au-dessus du sol.
            public void AddBox(Point3D c, Vector3D size)
            {
                double hx = size.X / 2, hy = size.Y / 2, hz = size.Z / 2;
                var p000 = new Point3D(c.X - hx, c.Y - hy, c.Z - hz);
                var p001 = new Point3D(c.X - hx, c.Y - hy, c.Z + hz);
                var p010 = new Point3D(c.X - hx, c.Y + hy, c.Z - hz);
                var p011 = new Point3D(c.X - hx, c.Y + hy, c.Z + hz);
                var p100 = new Point3D(c.X + hx, c.Y - hy, c.Z - hz);
                var p101 = new Point3D(c.X + hx, c.Y - hy, c.Z + hz);
                var p110 = new Point3D(c.X + hx, c.Y + hy, c.Z - hz);
                var p111 = new Point3D(c.X + hx, c.Y + hy, c.Z + hz);

                AddQuad(p010, p011, p111, p110, new Vector3D(0, 1, 0));   // dessus
                AddQuad(p001, p101, p111, p011, new Vector3D(0, 0, 1));   // +Z
                AddQuad(p100, p000, p010, p110, new Vector3D(0, 0, -1));  // -Z
                AddQuad(p101, p100, p110, p111, new Vector3D(1, 0, 0));   // +X
                AddQuad(p000, p001, p011, p010, new Vector3D(-1, 0, 0));  // -X
            }

            // Boîte orientée entre deux points au sol (pont entre segments, tige de flèche).
            public void AddOrientedBox(Point3D a, Point3D b, double width, double height, double baseY = 0)
            {
                var d = new Vector3D(b.X - a.X, 0, b.Z - a.Z);
                double len = d.Length;
                if (len < 1e-4) return;
                d /= len;

                var sn = new Vector3D(-d.Z, 0, d.X);
                var s = sn * (width / 2);
                var up = new Vector3D(0, height, 0);
                var a0 = new Point3D(a.X, baseY, a.Z);
                var b0 = new Point3D(b.X, baseY, b.Z);
                Point3D a1 = a0 - s, a2 = a0 + s, b1 = b0 - s, b2 = b0 + s;

                AddQuadFacing(a1 + up, a2 + up, b2 + up, b1 + up, new Vector3D(0, 1, 0));
                AddQuadFacing(a2, b2, b2 + up, a2 + up, sn);
                AddQuadFacing(a1, a1 + up, b1 + up, b1, -sn);
                AddQuadFacing(b1, b1 + up, b2 + up, b2, d);
                AddQuadFacing(a1, a2, a2 + up, a1 + up, -d);
            }

            public void AddTriangleUp(Point3D a, Point3D b, Point3D c, double y)
            {
                a.Y = y; b.Y = y; c.Y = y;
                if (Vector3D.CrossProduct(b - a, c - a).Y < 0) { var tmp = b; b = c; c = tmp; }
                int i = _positions.Count;
                _positions.Add(a); _positions.Add(b); _positions.Add(c);
                var n = new Vector3D(0, 1, 0);
                _normals.Add(n); _normals.Add(n); _normals.Add(n);
                _indices.Add(i); _indices.Add(i + 1); _indices.Add(i + 2);
            }

            public void AddDiscUp(Point3D c, double r, double y, int segments)
            {
                if (r <= 0) return;
                var n = new Vector3D(0, 1, 0);
                int center = _positions.Count;
                _positions.Add(new Point3D(c.X, y, c.Z));
                _normals.Add(n);
                for (int j = 0; j <= segments; j++)
                {
                    double a = 2 * Math.PI * j / segments;
                    _positions.Add(new Point3D(c.X + r * Math.Cos(a), y, c.Z + r * Math.Sin(a)));
                    _normals.Add(n);
                }
                for (int j = 0; j < segments; j++)
                {
                    _indices.Add(center); _indices.Add(center + j + 2); _indices.Add(center + j + 1);
                }
            }

            public void AddSphere(Point3D c, double r, int slices, int stacks)
            {
                int start = _positions.Count;
                for (int i = 0; i <= stacks; i++)
                {
                    double phi = Math.PI * i / stacks;
                    double y = Math.Cos(phi), sr = Math.Sin(phi);
                    for (int j = 0; j <= slices; j++)
                    {
                        double th = 2 * Math.PI * j / slices;
                        var n = new Vector3D(sr * Math.Cos(th), y, sr * Math.Sin(th));
                        _positions.Add(c + n * r);
                        _normals.Add(n);
                    }
                }

                int row = slices + 1;
                for (int i = 0; i < stacks; i++)
                    for (int j = 0; j < slices; j++)
                    {
                        int a = start + i * row + j, b = a + row;
                        _indices.Add(a); _indices.Add(a + 1); _indices.Add(b);
                        _indices.Add(a + 1); _indices.Add(b + 1); _indices.Add(b);
                    }
            }

            private void AddQuadFacing(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
            {
                if (Vector3D.DotProduct(Vector3D.CrossProduct(b - a, c - a), n) < 0)
                    AddQuad(a, d, c, b, n);
                else
                    AddQuad(a, b, c, d, n);
            }

            // Sommets dans le sens antihoraire vus depuis l'extérieur (côté de la normale).
            private void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D n)
            {
                int i = _positions.Count;
                _positions.Add(a); _positions.Add(b); _positions.Add(c); _positions.Add(d);
                _normals.Add(n); _normals.Add(n); _normals.Add(n); _normals.Add(n);
                _indices.Add(i); _indices.Add(i + 1); _indices.Add(i + 2);
                _indices.Add(i); _indices.Add(i + 2); _indices.Add(i + 3);
            }

            public MeshGeometry3D ToMesh()
            {
                var mesh = new MeshGeometry3D
                {
                    Positions = _positions,
                    Normals = _normals,
                    TriangleIndices = _indices
                };
                mesh.Freeze();
                return mesh;
            }

            public GeometryModel3D ToModel(Material material, bool freezeModel = true)
            {
                var model = new GeometryModel3D(ToMesh(), material);
                if (freezeModel) model.Freeze();
                return model;
            }
        }
    }
}
