using System;
using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.Enums;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694's single custom auto align - handles reef branch scoring, the human player station, the barge,
    /// and the processor, the same way 340's GRRAutoAlign is one self-contained component rather than
    /// relying on the shared framework AutoAlign. It replaces the framework's ReefscapeAutoAlign component
    /// for this robot.
    ///
    /// Station and barge align both work the same way: an AlignZone is a corner-to-corner line (its
    /// rotation is the heading to face). Rotation and the distance perpendicular to that line are always
    /// PID-corrected, but the position *along* the line is a slider that starts centered the moment you
    /// engage and can be nudged toward either corner with the left stick, clamped so you can't slide past
    /// either end - "aligns to the middle, but lets you slide across it to either corner while it holds you
    /// at the right distance away," per the corresponding real StuyPulse commands
    /// (github.com/StuyPulse/Aunt-Mary): SwerveDrivePIDAssistToClosestCoralStation for the station, and
    /// SwerveDriveDriveAlignedToBarge118Score for the barge (which locks distance-to-line + heading and
    /// leaves the driver's left stick fully in control of position along the line - this is a clamped,
    /// centered version of that same idea rather than the real robot's unclamped open-ended slide).
    /// Processor align is a FixedAlignTarget instead - a single fixed point with no slider, since there's no
    /// equivalent "slide along a line" concept for lining up with the processor.
    ///
    /// Barge and processor align both engage automatically while CurrentSetpoint is Barge/Processor and the
    /// driver is holding algae ready to score, while either AutoAlignLeft or AutoAlignRight is held - MoSim
    /// has no dedicated button for either, so both reuse the same "hold align" buttons the reef branch align
    /// uses, just routed to different behavior based on what setpoint you're currently in.
    ///
    /// Reef branch align keeps the exact same node-finding and offset-application logic the framework's
    /// ReefscapeAutoAlign used (closest ReefFace-tagged AlignNode, perspective-relative left/right,
    /// the same AutoAlignOffset assets already tuned for this robot) so existing tuned values keep working -
    /// it's just re-hosted here so the whole thing runs through one PID loop instead of two components
    /// fighting over the drivetrain.
    ///
    /// None of the three alignment modes use RobotFramework.Controllers.PidSystems.PIDController (the
    /// shared controller the joints are tuned through) - the translation/rotation PID loops below are
    /// implemented from scratch so a future change to that shared PID controller cannot change this
    /// component's behavior.
    ///
    /// This script doesn't hold its own references to the froggy coral stow / shooter algae stow
    /// GamePieceStates - it reads whether coral/algae is docked there through the sibling robot script's
    /// IStuyPulseGamePieceStatus instead, so that game-piece-system knowledge lives in one place.
    ///
    /// Station, barge, and processor align all route around the reef instead of cutting through it when the
    /// straight line to the target passes too close to the reef center (see ApplyReefAvoidance) - handles
    /// being on the far side of the reef from wherever you're headed. Each keeps its own independent
    /// routing/hysteresis state so they don't interfere with each other. The corner-to-corner slide on
    /// station/barge, and the reef branch left/right pick, all account for which way the active camera is
    /// facing so the stick always matches what's visually left/right on screen - same idea as 340's
    /// GRRAutoAlign camera-relative flip, just generalized (see ApplyCameraFlip and CameraFacesNode) instead
    /// of copying its field-axis-specific math.
    /// </summary>
    public class StuyPulseAutoAlign : MonoBehaviour
    {
        [Serializable]
        public class AlignZone
        {
            public Alliance alliance;

            [Tooltip("World-space position of one end of this align zone's line (e.g. one edge of the HP station opening, or one end of the barge line)")]
            public Vector3 leftCorner;

            [Tooltip("World-space position of the other end of this align zone's line")]
            public Vector3 rightCorner;

            [Tooltip("Robot heading (degrees) to face while aligned to this zone")]
            public float yRotation;
        }

        /// <summary>A single fixed align point - no corner-to-corner slider, unlike AlignZone.</summary>
        [Serializable]
        public class FixedAlignTarget
        {
            public Alliance alliance;

            [Tooltip("World-space position to align to")]
            public Vector3 position;

            [Tooltip("Robot heading (degrees) to face while aligned here")]
            public float yRotation;
        }

        /// <summary>
        /// A small, self-contained PID axis. Deliberately separate from RobotFramework.Controllers.PidSystems.PIDController
        /// so this component's tuning/behavior is fully isolated from that shared class.
        /// </summary>
        [Serializable]
        public class ManualPidAxis
        {
            public float kP = 3f;
            public float kI = 0f;
            public float kD = 0f;
            [Tooltip("Clamps the accumulated integral term to prevent windup")]
            public float integralLimit = 0.5f;

            private float _integral;
            private float _lastError;
            private bool _hasLastError;

            public float Update(float error, float dt)
            {
                _integral = Mathf.Clamp(_integral + error * dt, -integralLimit, integralLimit);
                var derivative = _hasLastError && dt > 0f ? (error - _lastError) / dt : 0f;

                _lastError = error;
                _hasLastError = true;

                return kP * error + kI * _integral + kD * derivative;
            }

            public void Reset()
            {
                _integral = 0f;
                _hasLastError = false;
            }
        }

        [Header("Reef Avoidance (shared by station, barge, and processor align)")]
        [Tooltip("If a straight line from the robot to the target would pass this close (meters) to the reef center, route around it instead of cutting through - handles being on the far side of the reef from wherever you're headed.")]
        [SerializeField] private float reefAvoidRadius = 2.5f;

        [Tooltip("Once routing around the reef, require the straight path to clear by this multiple of reefAvoidRadius before switching back to a direct line - avoids flickering between routed/direct right at the boundary.")]
        [SerializeField] private float reefAvoidExitMargin = 1.3f;

        [Header("Human Player Station Align")]
        [Tooltip("One AlignZone (left corner, right corner, facing rotation) per physical coral station slide line on the field - e.g. 4 entries for 2 stations x 2 alliances. No other fallback; a station with nothing here in range simply won't align.")]
        [SerializeField] private AlignZone[] stationTargets;

        [Tooltip("Only assist toward the station within this distance (feet)")]
        [SerializeField] private float maxStationAlignDistanceFeet = 12f;

        [Tooltip("How fast (world units/sec at full stick deflection) the slide target moves along the station line")]
        [SerializeField] private float stationSlideSpeed = 1.5f;

        [Header("Barge Align")]
        [Tooltip("One entry per alliance's barge line. Takes priority over the auto-derived BargeScorer fallback below when something here is in range.")]
        [SerializeField] private AlignZone[] bargeTargets;

        [Tooltip("Only assist toward the barge within this distance (feet)")]
        [SerializeField] private float maxBargeAlignDistanceFeet = 20f;

        [Tooltip("How fast (world units/sec at full stick deflection) the slide target moves along the barge line")]
        [SerializeField] private float bargeSlideSpeed = 2.5f;

        [Tooltip("Standoff distance (inches) from the barge along its local right axis, for the auto-derived barge zone. Only used when nothing in bargeTargets is in range. Taken directly from the real robot's own constant (TARGET_DISTANCE_FROM_CENTERLINE_FOR_BARGE_118) - real, not a guess.")]
        [SerializeField] private float bargeStandoffInches = 118f;

        [Tooltip("Half-width (inches) of the auto-derived barge slide line along its local forward axis - still a best-guess estimate, not yet verified against the actual barge mesh.")]
        [SerializeField] private float bargeHalfWidthInches = 40f;

        [Tooltip("Extra position correction (inches) applied on top of the derived barge zone, in the barge's own local axes: X = toward/away from the barge (positive = further, same axis as bargeStandoffInches), Y = height, Z = shifts the whole slide line left/right along the barge (same axis as bargeHalfWidthInches). Use this to fine-tune distance/position in Play mode without touching the base geometry constants above.")]
        [SerializeField] private Vector3 bargeOffsetInches = Vector3.zero;

        [Tooltip("Extra heading offset (degrees) added on top of the derived barge facing rotation - use this to fix the approach angle.")]
        [SerializeField] private float bargeRotationOffsetDegrees = 0f;

        [Header("Processor Align")]
        [Tooltip("One entry per alliance's processor - a single fixed point, no slider (unlike station/barge)")]
        [SerializeField] private FixedAlignTarget[] processorTargets;

        [Tooltip("Only assist toward the processor within this distance (feet)")]
        [SerializeField] private float maxProcessorAlignDistanceFeet = 12f;

        [Header("Reef Branch Align")]
        [SerializeField] private AutoAlignOffset l1offset;
        [SerializeField] private AutoAlignOffset frontLeftOffset;
        [SerializeField] private AutoAlignOffset frontRightOffset;
        [SerializeField] private AutoAlignOffset backLeftOffset;
        [SerializeField] private AutoAlignOffset backRightOffset;
        [SerializeField] private AutoAlignOffset frontLeftL4Offset;
        [SerializeField] private AutoAlignOffset frontRightL4Offset;
        [SerializeField] private AutoAlignOffset backLeftL4Offset;
        [SerializeField] private AutoAlignOffset backRightL4Offset;

        [Tooltip("Only assist toward the reef within this distance (feet)")]
        [SerializeField] private float maxReefAlignDistanceFeet = 25f;

        [Tooltip("Extra distance (inches) added to the reef offset's Z when IStuyPulseGamePieceStatus.WantsExtraReefClearance is true (right after L4, switching to Algae) - pushes the align target farther from the reef instead of holding the normal scoring distance. If this ends up pulling the robot closer instead of farther, flip the sign.")]
        [SerializeField] private float extraReefClearanceInches = 24f;

        [Header("Manual PID (self-contained, not the shared framework PIDController)")]
        [Tooltip("Defaults are carried over from the old ReefscapeAutoAlign component's tuned drivePID (kP 30, kI 0.1, kD 1.65, Isaturation 1) - both X and Z used the same drivePID there too.")]
        [SerializeField] private ManualPidAxis translateX = new ManualPidAxis { kP = 30f, kI = 0.1f, kD = 1.65f, integralLimit = 1f };
        [SerializeField] private ManualPidAxis translateZ = new ManualPidAxis { kP = 30f, kI = 0.1f, kD = 1.65f, integralLimit = 1f };

        [Tooltip("Defaults are carried over from the old ReefscapeAutoAlign component's tuned rotatePID (kP 0.1, kI 0, kD 0.003, Isaturation 1)")]
        [SerializeField] private ManualPidAxis rotate = new ManualPidAxis { kP = 0.1f, kI = 0f, kD = 0.003f, integralLimit = 1f };

        [Tooltip("Clamps the combined X/Z drive output to the same -1..1 range the joystick drive input uses (old drivePID.Max was 1)")]
        [SerializeField] private float maxTranslateOutput = 1f;

        [Tooltip("Clamps the rotate output (old rotatePID.Max was 0.75)")]
        [SerializeField] private float maxRotateOutput = 0.75f;

        private const float FEET_TO_METERS = 0.3048f;
        private const float INCHES_TO_METERS = 0.0254f;
        private const float MIN_LINE_LENGTH = 0.01f;

        private ReefscapeRobotBase _stuyBase;
        private DriveController _driveController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;
        private IStuyPulseGamePieceStatus _gamePieceStatus;

        private readonly List<AlignNode> _reefFaces = new();
        private readonly Dictionary<Transform, AlignNode> _reefNodeParents = new();
        private Transform _closestReefNode;
        private Transform _secondClosestReefNode;

        private readonly List<BargeScorer> _bargeScorers = new();

        private Vector3 _blueReefPos;
        private Vector3 _redReefPos;
        private bool _hasReefPos;

        private bool _stationEngaged;
        private float _stationSlide = 0.5f;
        private bool _stationRoutingAroundReef;

        private bool _bargeEngaged;
        private float _bargeSlide = 0.5f;
        private bool _bargeRoutingAroundReef;

        private bool _processorRoutingAroundReef;

        private bool _stationAlignActive;
        private bool _bargeAlignActive;
        private bool _processorAlignActive;
        private bool _reefAlignActive;
        private bool _reefAlignLeft;

        private void Awake()
        {
            _stuyBase = GetComponent<ReefscapeRobotBase>();
            _driveController = GetComponent<DriveController>();
            _pieces = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();
            _gamePieceStatus = GetComponent<IStuyPulseGamePieceStatus>();
        }

        private void Start()
        {
            foreach (var faceObject in GameObject.FindGameObjectsWithTag("ReefFace"))
            {
                if (!faceObject.TryGetComponent<AlignNode>(out var face)) continue;
                _reefFaces.Add(face);
                _reefNodeParents.TryAdd(face.LeftNode.transform, face);
                _reefNodeParents.TryAdd(face.RightNode.transform, face);
            }

            var blueReef = GameObject.Find("BlueReef");
            var redReef = GameObject.Find("RedReef");
            if (blueReef != null && redReef != null)
            {
                _blueReefPos = blueReef.transform.position;
                _redReefPos = redReef.transform.position;
                _hasReefPos = true;
            }

            // Same object-by-object cast pattern ReefscapeRobotBase itself uses for this non-generic overload.
            foreach (var found in FindObjectsByType(typeof(BargeScorer), FindObjectsSortMode.None))
            {
                if (found is BargeScorer scorer) _bargeScorers.Add(scorer);
            }
        }

        private void Update()
        {
            if (_stuyBase == null) return;

            // Only re-pick the closest reef faces on the button press edge, same as the framework's
            // ReefscapeAutoAlign - stops the target from jumping to a different branch mid-align.
            if (_stuyBase.AutoAlignLeftAction.triggered || _stuyBase.AutoAlignRightAction.triggered)
            {
                (_closestReefNode, _secondClosestReefNode) = FindClosestReefNodes();
            }
        }

        private void FixedUpdate()
        {
            var wasActive = _stationAlignActive || _bargeAlignActive || _processorAlignActive || _reefAlignActive;

            if (TryGetBargeAlignTarget(out var bargeTarget, out var bargeYaw))
            {
                _bargeAlignActive = true;
                _processorAlignActive = false;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(bargeTarget, bargeYaw);
                return;
            }

            _bargeAlignActive = false;

            if (TryGetProcessorAlignTarget(out var processorTarget, out var processorYaw))
            {
                _processorAlignActive = true;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(processorTarget, processorYaw);
                return;
            }

            _processorAlignActive = false;

            if (TryGetReefAlignTarget(out var reefTarget, out var reefYaw))
            {
                _reefAlignActive = true;
                _stationAlignActive = false;
                DriveManualPid(reefTarget, reefYaw);
                return;
            }

            _reefAlignActive = false;

            if (TryGetStationAlignTarget(out var stationTarget, out var stationYaw))
            {
                _stationAlignActive = true;
                DriveManualPid(stationTarget, stationYaw);
                return;
            }

            _stationAlignActive = false;

            if (wasActive) ResetPid();
        }

        // ---- Human player station ----

        private bool TryGetStationAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null) { _stationEngaged = false; return false; }

            if (_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed()) { _stationEngaged = false; return false; }
            if (_stuyBase.CurrentRobotMode != ReefscapeRobotMode.Coral) { _stationEngaged = false; return false; }
            if (_gamePieceStatus != null && _gamePieceStatus.IsIntakingAlgae) { _stationEngaged = false; return false; }
            if (_stuyBase.CurrentIntakeMode != ReefscapeIntakeMode.Normal) { _stationEngaged = false; return false; }
            if (!_stuyBase.IntakeAction.IsPressed()) { _stationEngaged = false; return false; }

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            if (coral != null && coral.HasPiece()) { _stationEngaged = false; return false; }

            var zone = GetClosestZone(stationTargets, _stuyBase.Alliance);
            if (zone == null) { _stationEngaged = false; return false; }

            if (!TryGetZoneTarget(zone, maxStationAlignDistanceFeet, stationSlideSpeed, ref _stationEngaged, ref _stationSlide, out targetPosition))
            {
                return false;
            }

            targetPosition = ApplyReefAvoidance(targetPosition, ref _stationRoutingAroundReef);
            targetYaw = zone.yRotation;
            return true;
        }

        // If you're on the far side of the reef from your target, a straight line to it would cut through the
        // reef structure - detect that and redirect through a waypoint that curves around it on whichever
        // side the robot is already leaning toward, instead of cutting the corner. Used by station, barge,
        // and processor align (each keeps its own routing/hysteresis state via the ref bool, since only one
        // of them can be driving at a time but their "are we currently routed around" state shouldn't bleed
        // together).
        private Vector3 ApplyReefAvoidance(Vector3 realTarget, ref bool routingAroundReef)
        {
            if (!_hasReefPos) return realTarget;

            var reefPos = _stuyBase.Alliance == Alliance.Blue ? _blueReefPos : _redReefPos;
            var robotPos = transform.position;

            var toTarget = realTarget - robotPos;
            toTarget.y = 0f;
            var distanceToTarget = toTarget.magnitude;

            if (distanceToTarget < reefAvoidRadius)
            {
                routingAroundReef = false;
                return realTarget;
            }

            var lineDir = toTarget / distanceToTarget;
            var toReef = reefPos - robotPos;
            toReef.y = 0f;
            var projection = Mathf.Clamp(Vector3.Dot(toReef, lineDir), 0f, distanceToTarget);
            var closestPointOnLine = robotPos + lineDir * projection;
            var reefClearance = Vector3.Distance(closestPointOnLine, reefPos);

            var exitThreshold = reefAvoidRadius * reefAvoidExitMargin;
            var shouldRoute = routingAroundReef ? reefClearance < exitThreshold : reefClearance < reefAvoidRadius;
            routingAroundReef = shouldRoute;

            if (!shouldRoute) return realTarget;

            var perpendicular = Vector3.Cross(Vector3.up, lineDir).normalized;
            var side = Vector3.Dot(robotPos - reefPos, perpendicular) >= 0f ? 1f : -1f;
            var waypoint = reefPos + perpendicular * side * reefAvoidRadius;
            waypoint.y = robotPos.y;
            return waypoint;
        }

        // ---- Barge ----

        private bool TryGetBargeAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null) { _bargeEngaged = false; return false; }

            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.Barge) { _bargeEngaged = false; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _bargeEngaged = false; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.HasShooterAlgae) { _bargeEngaged = false; return false; }

            // Hand-placed zones take priority; only fall back to deriving one (from the nearest same-alliance
            // BargeScorer, picking whichever side of it the robot is currently closer to) if nothing
            // hand-placed is in range.
            var zone = GetClosestZone(bargeTargets, _stuyBase.Alliance) ?? DeriveClosestBargeZone();
            if (zone == null) { _bargeEngaged = false; return false; }

            if (!TryGetZoneTarget(zone, maxBargeAlignDistanceFeet, bargeSlideSpeed, ref _bargeEngaged, ref _bargeSlide, out targetPosition))
            {
                return false;
            }

            targetPosition = ApplyReefAvoidance(targetPosition, ref _bargeRoutingAroundReef);
            targetYaw = zone.yRotation;
            return true;
        }

        // ---- Processor ----

        private bool TryGetProcessorAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null || processorTargets == null || processorTargets.Length == 0) return false;
            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.Processor) return false;
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) return false;
            if (_gamePieceStatus == null || !_gamePieceStatus.HasShooterAlgae) return false;

            var target = GetClosestFixedTarget(processorTargets, _stuyBase.Alliance);
            if (target == null) return false;

            var robotXZ = new Vector2(transform.position.x, transform.position.z);
            var targetXZ = new Vector2(target.position.x, target.position.z);
            if (Vector2.Distance(robotXZ, targetXZ) > maxProcessorAlignDistanceFeet * FEET_TO_METERS) return false;

            targetPosition = ApplyReefAvoidance(target.position, ref _processorRoutingAroundReef);
            targetYaw = target.yRotation;
            return true;
        }

        private FixedAlignTarget GetClosestFixedTarget(FixedAlignTarget[] targets, Alliance alliance)
        {
            FixedAlignTarget closest = null;
            var closestDistance = float.MaxValue;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);

            foreach (var target in targets)
            {
                if (target == null || target.alliance != alliance) continue;

                var distance = Vector2.Distance(robotXZ, new Vector2(target.position.x, target.position.z));
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = target;
                }
            }

            return closest;
        }

        // ---- Shared corner-to-corner slide logic used by both station and barge align ----

        private bool TryGetZoneTarget(AlignZone zone, float maxDistanceFeet, float slideSpeed, ref bool engaged, ref float slide, out Vector3 targetPosition)
        {
            targetPosition = Vector3.zero;

            var lineLength = Vector3.Distance(zone.leftCorner, zone.rightCorner);
            if (lineLength < MIN_LINE_LENGTH)
            {
                engaged = false;
                return false;
            }

            var center = (zone.leftCorner + zone.rightCorner) * 0.5f;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);
            var centerXZ = new Vector2(center.x, center.z);
            if (Vector2.Distance(robotXZ, centerXZ) > maxDistanceFeet * FEET_TO_METERS)
            {
                engaged = false;
                return false;
            }

            // Fresh engage (button/context just became true this frame) starts the slide at whichever point
            // on the line is closest to the robot right now, not the middle - so first pressing the button
            // never yanks the target sideways before the driver's stick input takes over.
            if (!engaged)
            {
                var lineVector = zone.rightCorner - zone.leftCorner;
                var t = Vector3.Dot(transform.position - zone.leftCorner, lineVector) / (lineLength * lineLength);
                slide = Mathf.Clamp01(t);
            }
            engaged = true;

            var rawStick = _stuyBase.TranslateAction.ReadValue<Vector2>().x;
            var stick = ApplyCameraFlip(rawStick, zone.rightCorner - zone.leftCorner);
            slide = Mathf.Clamp01(slide + stick * slideSpeed * Time.fixedDeltaTime / lineLength);

            targetPosition = Vector3.Lerp(zone.leftCorner, zone.rightCorner, slide);
            return true;
        }

        // Same idea as 340's GRRAutoAlign camera-relative flip (it XORs a "camera facing -X" check into its
        // left/right decision so the button always matches what the driver sees on screen) - generalized here
        // to any line direction instead of GRR's field-axis-specific heuristic: if the active camera's screen
        // right doesn't point the same way as leftCorner->rightCorner in world space, the stick is inverted so
        // pushing right always slides the target toward whatever looks like "right" on screen.
        private float ApplyCameraFlip(float stickValue, Vector3 lineDirection)
        {
            var camera = _stuyBase.GetActiveCamera();
            if (camera == null) return stickValue;

            var cameraRight = camera.transform.right;
            cameraRight.y = 0f;
            if (cameraRight.sqrMagnitude < 0.0001f) return stickValue;

            var flatLine = new Vector3(lineDirection.x, 0f, lineDirection.z);
            if (flatLine.sqrMagnitude < 0.0001f) return stickValue;

            return Vector3.Dot(cameraRight.normalized, flatLine.normalized) >= 0f ? stickValue : -stickValue;
        }

        private AlignZone GetClosestZone(AlignZone[] zones, Alliance alliance)
        {
            if (zones == null) return null;

            AlignZone closest = null;
            var closestDistance = float.MaxValue;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);

            foreach (var zone in zones)
            {
                if (zone == null || zone.alliance != alliance) continue;

                var center = (zone.leftCorner + zone.rightCorner) * 0.5f;
                var distance = Vector2.Distance(robotXZ, new Vector2(center.x, center.z));

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = zone;
                }
            }

            return closest;
        }

        // ---- Deriving barge zones from scene objects, no inspector wiring needed ----

        // The barge can be approached from either side (defense/collisions can easily push the robot to the
        // "wrong" side mid-match), so this picks whichever of the two standoff points along the scorer's
        // forward axis the robot is currently closer to, every time it's called - not just once at Start.
        private AlignZone DeriveClosestBargeZone()
        {
            BargeScorer closest = null;
            var closestDistance = float.MaxValue;

            foreach (var scorer in _bargeScorers)
            {
                if (scorer == null || scorer.Alliance != _stuyBase.Alliance) continue;

                var distance = Vector3.Distance(transform.position, scorer.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = scorer;
                }
            }

            if (closest == null) return null;

            var reference = closest.transform;
            var standoff = bargeStandoffInches * INCHES_TO_METERS;
            var halfWidth = bargeHalfWidthInches * INCHES_TO_METERS;

            // BargeScorer's own BoxCollider (on this same transform, confirmed in Barge.prefab) is narrow
            // along its local right axis (~1m) and long along its local forward axis (~3.7m) - so the
            // approach/standoff direction is right, and the corner-to-corner slide line runs along forward.
            // This was previously swapped, which put the slider on the wrong axis entirely.
            var sideACenter = reference.position + reference.right * standoff;
            var sideBCenter = reference.position - reference.right * standoff;
            var useSideA = Vector3.Distance(transform.position, sideACenter) <= Vector3.Distance(transform.position, sideBCenter);

            // Two hardcoded corner points (forward/back along the barge from whichever standoff side is
            // closer) define the entire slide line - built once from the chosen side's center, not derived
            // from any further per-frame axis math.
            var center = useSideA ? sideACenter : sideBCenter;
            var faceDirection = useSideA ? -reference.right : reference.right;

            // bargeOffsetInches is a tunable correction on top of the derived geometry above, not a
            // replacement for it - X rides the same standoff axis (sign-flipped per side so positive X
            // always means "further from the barge" regardless of which side was picked), Y is height, Z
            // rides the same half-width axis and shifts both corners together.
            var standoffSign = useSideA ? 1f : -1f;
            var offsetWorld = reference.right * (standoffSign * bargeOffsetInches.x * INCHES_TO_METERS)
                             + reference.up * (bargeOffsetInches.y * INCHES_TO_METERS)
                             + reference.forward * (bargeOffsetInches.z * INCHES_TO_METERS);
            center += offsetWorld;

            var leftCorner = center - reference.forward * halfWidth;
            var rightCorner = center + reference.forward * halfWidth;

            return new AlignZone
            {
                alliance = _stuyBase.Alliance,
                leftCorner = leftCorner,
                rightCorner = rightCorner,
                yRotation = Quaternion.LookRotation(faceDirection, Vector3.up).eulerAngles.y + bargeRotationOffsetDegrees
            };
        }

        // ---- Reef branch ----

        private bool TryGetReefAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;
            _reefAlignLeft = false;

            if (_stuyBase == null || _driveController == null) return false;

            var pressedLeft = _stuyBase.AutoAlignLeftAction.IsPressed();
            var pressedRight = _stuyBase.AutoAlignRightAction.IsPressed();
            if (!pressedLeft && !pressedRight) return false;
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Place) return false;
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Barge) return false;

            var usePerspective = PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1;
            var cameraFacesLeftNode = usePerspective && _closestReefNode != null &&
                                       _reefNodeParents.TryGetValue(_closestReefNode, out var parentForCamera) &&
                                       CameraFacesNode(parentForCamera);

            // Perspective mode flips which physical side "left" refers to depending on which way the
            // camera is looking, same as the framework's ReefscapeAutoAlign.
            var wantsLeftSide = pressedLeft
                ? (usePerspective ? !cameraFacesLeftNode : true)
                : (usePerspective && cameraFacesLeftNode);

            if (TryAlignToReefNode(_closestReefNode, wantsLeftSide, out targetPosition, out targetYaw)) return true;
            if (TryAlignToReefNode(_secondClosestReefNode, wantsLeftSide, out targetPosition, out targetYaw)) return true;

            if (_closestReefNode != null && _reefNodeParents.TryGetValue(_closestReefNode, out var closestParent))
            {
                if (TryAlignToReefNode(closestParent.LeftNode.transform, wantsLeftSide, out targetPosition, out targetYaw)) return true;
                if (TryAlignToReefNode(closestParent.RightNode.transform, wantsLeftSide, out targetPosition, out targetYaw)) return true;
            }

            return false;
        }

        private bool TryAlignToReefNode(Transform node, bool wantsLeftSide, out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (node == null || !_reefNodeParents.TryGetValue(node, out var parent)) return false;

            var isCorrectSide = wantsLeftSide ? parent.LeftNode.transform == node : parent.RightNode == node.gameObject;
            if (!isCorrectSide) return false;

            if (Vector3.Distance(transform.position, node.position) > maxReefAlignDistanceFeet * FEET_TO_METERS) return false;

            var holdingFroggyCoral = _gamePieceStatus != null && _gamePieceStatus.HasFroggyCoral;

            var offset = holdingFroggyCoral ? l1offset : GetScoringOffset(wantsLeftSide);
            if (offset == null) return false;

            var facingReef = IsFacingReef();
            var targetRotation = node.rotation;
            if (!facingReef && !holdingFroggyCoral) targetRotation *= Quaternion.Euler(0, 180, 0);

            // Right after L4, if the driver switches to Algae mode, hold a bigger standoff distance instead of
            // the normal scoring distance - otherwise holding the align button pins the robot close to the
            // reef the whole time and the arm never gets room to reposition for algae.
            var wantsExtraClearance = _gamePieceStatus != null && _gamePieceStatus.WantsExtraReefClearance;
            var zOffset = offset.zOffset + (wantsExtraClearance ? extraReefClearanceInches : 0f);

            var localOffset = new Vector3(offset.xOffset, offset.yOffset, zOffset) * INCHES_TO_METERS;
            targetPosition = node.position + targetRotation * localOffset;

            targetRotation *= Quaternion.Euler(0, offset.Rotation, 0);
            targetYaw = targetRotation.eulerAngles.y;

            _reefAlignLeft = wantsLeftSide;
            return true;
        }

        private AutoAlignOffset GetScoringOffset(bool isLeftSide)
        {
            var isL4 = _stuyBase.CurrentSetpoint == ReefscapeSetpoints.L4;
            var facingReef = IsFacingReef();

            if (facingReef) return isLeftSide ? (isL4 ? frontLeftL4Offset : frontLeftOffset) : (isL4 ? frontRightL4Offset : frontRightOffset);
            return isLeftSide ? (isL4 ? backLeftL4Offset : backLeftOffset) : (isL4 ? backRightL4Offset : backRightOffset);
        }

        private (Transform closest, Transform secondClosest) FindClosestReefNodes()
        {
            AlignNode closestFace = null;
            AlignNode secondClosestFace = null;
            var closestDist = float.MaxValue;
            var secondClosestDist = float.MaxValue;

            foreach (var face in _reefFaces)
            {
                if (face == null || face.transform == null) continue;

                var dist = Vector3.Distance(transform.position, face.transform.position);
                if (dist < closestDist)
                {
                    secondClosestDist = closestDist;
                    secondClosestFace = closestFace;
                    closestDist = dist;
                    closestFace = face;
                }
                else if (dist < secondClosestDist)
                {
                    secondClosestDist = dist;
                    secondClosestFace = face;
                }
            }

            if (closestFace == null || secondClosestFace == null) return (null, null);

            var candidates = new[]
            {
                closestFace.LeftNode.transform, closestFace.RightNode.transform,
                secondClosestFace.LeftNode.transform, secondClosestFace.RightNode.transform
            };

            Transform best = null;
            Transform secondBest = null;
            var bestDist = float.MaxValue;
            var secondBestDist = float.MaxValue;

            foreach (var candidate in candidates)
            {
                var dist = Vector3.Distance(transform.position, candidate.position);
                if (dist < bestDist)
                {
                    secondBestDist = bestDist;
                    secondBest = best;
                    bestDist = dist;
                    best = candidate;
                }
                else if (dist < secondBestDist)
                {
                    secondBestDist = dist;
                    secondBest = candidate;
                }
            }

            return (best, secondBest);
        }

        private bool CameraFacesNode(AlignNode node)
        {
            var camera = _stuyBase.GetActiveCamera();
            if (camera == null) return false;
            return Vector3.Dot(camera.transform.forward, node.transform.forward) > 0;
        }

        private bool IsFacingReef()
        {
            return _stuyBase.GetFacingReef();
        }

        // ---- Shared PID drive ----

        private void DriveManualPid(Vector3 targetPosition, float targetYawDegrees)
        {
            var dt = Time.fixedDeltaTime;

            var errorX = targetPosition.x - transform.position.x;
            var errorZ = targetPosition.z - transform.position.z;

            var outputX = translateX.Update(errorX, dt);
            var outputZ = translateZ.Update(errorZ, dt);

            var translateOutput = new Vector2(outputX, outputZ);
            if (translateOutput.magnitude > maxTranslateOutput)
            {
                translateOutput = translateOutput.normalized * maxTranslateOutput;
            }

            var currentYaw = ToMathYaw(transform.eulerAngles.y);
            var targetYaw = ToMathYaw(targetYawDegrees);
            var angleError = Mathf.Repeat(targetYaw - currentYaw + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            // rotate's gain is tuned for degrees (carried over from the old degree-based PIDController.UpdateAngle),
            // but angleError above is in radians (ToMathYaw's convention) - convert before feeding the PID or the
            // proportional term ends up ~57x (1/Rad2Deg) too weak, which is why turning was so slow.
            var angleErrorDegrees = angleError * Mathf.Rad2Deg;
            var rotateOutput = Mathf.Clamp(rotate.Update(angleErrorDegrees, dt), -maxRotateOutput, maxRotateOutput);

            _driveController.overideInput(translateOutput, rotateOutput, DriveController.DriveMode.FieldOriented);
        }

        private void ResetPid()
        {
            translateX.Reset();
            translateZ.Reset();
            rotate.Reset();
        }

        // Matches the yaw convention used by 340's proven GRRAutoAlign: converts Unity's left-handed Y euler
        // (0 = +Z, clockwise-positive) into a standard math angle (0 = +X, counter-clockwise-positive) so the
        // wrapped angle error can be computed the same well-tested way.
        private static float ToMathYaw(float unityYawDegrees)
        {
            return -Mathf.Deg2Rad * (unityYawDegrees - 90f);
        }

        /// <summary>True while this component is actively driving the robot toward a human player station.</summary>
        public bool StationAlignActive() => _stationAlignActive;

        /// <summary>True while this component is actively driving the robot toward the barge.</summary>
        public bool BargeAlignActive() => _bargeAlignActive;

        /// <summary>True while this component is actively driving the robot toward the processor.</summary>
        public bool ProcessorAlignActive() => _processorAlignActive;

        /// <summary>True while this component is actively driving the robot toward a reef branch.</summary>
        public bool ReefAlignActive() => _reefAlignActive;

        /// <summary>True if the reef branch currently being targeted is the left one.</summary>
        public bool ReefAlignLeft() => _reefAlignLeft;
    }
}
