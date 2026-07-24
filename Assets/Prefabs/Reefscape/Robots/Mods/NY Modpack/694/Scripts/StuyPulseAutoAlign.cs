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

            [Tooltip("Robot heading (degrees) to face at this SAME position when scoring an algae that's held in froggy instead of the shooter - froggy releases it in a different direction than the shooter does, so it needs its own heading here.")]
            public float froggyAlgaeIntakeYRotation;
        }

        // A scene "Algae" GameObject matched to whichever reef face it spawned nearest, plus whether it's
        // the Low or High piece on that face and where it started (see TryGetAlgaeAlignTarget for why the
        // start position matters). Not [Serializable]/inspector-facing - built once in Start() from scene
        // objects, same as _bargeScorers/_reefFaces.
        private class AlgaeSpot
        {
            public Transform pieceTransform;
            public Vector3 spawnPosition;
            public bool isHigh;
            public AlignNode face;
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

        [Tooltip("Only matters for a barge line that spans the field midline (x=0). Keeps the slide target from landing within this many meters of the x=0 crossing point on either side, so it jumps across the midline instead of parking on it")]
        [SerializeField] private float bargeSlideMidlineGapMeters = 0.2f;

        [Tooltip("Standoff distance (inches) from the barge along its local right axis, for the auto-derived barge zone. Only used when nothing in bargeTargets is in range. Taken directly from the real robot's own constant (TARGET_DISTANCE_FROM_CENTERLINE_FOR_BARGE_118) - real, not a guess.")]
        [SerializeField] private float bargeStandoffInches = 118f;

        [Tooltip("Half-width (inches) of the auto-derived barge slide line along its local forward axis - still a best-guess estimate, not yet verified against the actual barge mesh.")]
        [SerializeField] private float bargeHalfWidthInches = 40f;

        [Tooltip("Extra position correction (inches) applied on top of the derived barge zone, in the barge's own local axes: X = toward/away from the barge (positive = further, same axis as bargeStandoffInches), Y = height, Z = shifts the whole slide line left/right along the barge (same axis as bargeHalfWidthInches). Use this to fine-tune distance/position in Play mode without touching the base geometry constants above.")]
        [SerializeField] private Vector3 bargeOffsetInches = Vector3.zero;

        [Tooltip("Extra heading offset (degrees) added on top of the derived barge facing rotation - use this to fix the approach angle.")]
        [SerializeField] private float bargeRotationOffsetDegrees = 0f;

        [Header("Processor Align")]
        [Tooltip("One entry per alliance's processor - a single fixed point, no slider (unlike station/barge). Each entry also carries a separate froggyAlgaeIntakeYRotation heading, used instead of yRotation when the held algae is in froggy rather than the shooter.")]
        [SerializeField] private FixedAlignTarget[] processorTargets;

        [Header("Algae Align")]
        [Tooltip("Only assist toward reef algae within this distance (feet)")]
        [SerializeField] private float maxAlgaeAlignDistanceFeet = 12f;

        [Tooltip("Standoff distance (inches) straight out from the reef face, along its outward-facing axis, that algae align holds. Best-guess default - tune this in Play mode. If it pulls the robot into the reef instead of away from it, flip the sign.")]
        [SerializeField] private float algaeStandoffInches = 24f;

        [Tooltip("Standoff distance (inches), same axis as algaeStandoffInches, held instead of it while the elevator/arm hasn't yet reached the algae setpoint - meant to be farther back so the robot isn't sitting up against the reef while the mechanism is still moving into position. Once the superstructure reaches the setpoint, align pulls in to algaeStandoffInches.")]
        [SerializeField] private float algaeStandoffNotReadyInches = 36f;

        [Tooltip("Left-right offset (inches, positive = robot's right while facing the target) applied on top of the centered algae target, used when approaching front-on (facing the reef). Separate from the back-approach offset below since the mechanism isn't necessarily centered the same way from both sides.")]
        [SerializeField] private float algaeFrontOffsetInches = 0f;

        [Tooltip("Same as algaeFrontOffsetInches, but applied when approaching back-first (not facing the reef).")]
        [SerializeField] private float algaeBackOffsetInches = 0f;

        [Header("Reef Branch Align")]
        [Tooltip("Total distance (inches) the driver can slide the L1/froggy scoring target left-right along the reef face with the translate stick, centered on l1offset - e.g. 6 means +/-3in from the default spot. Releasing the stick does not recenter; only a fresh press of the align button (or leaving the reef and coming back) resets to the default offset.")]
        [SerializeField] private float l1SlideRangeInches = 6f;

        [Tooltip("How fast (inches/sec at full stick deflection) the L1/froggy slide target moves within l1SlideRangeInches.")]
        [SerializeField] private float l1SlideSpeed = 6f;

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

        // How far ahead (in degrees, around the reef circle) the reef-avoidance waypoint leads the robot's
        // own current angular position - see ApplyReefAvoidance's "leading tangent" comment. Untested value,
        // a reasonable-looking guess; if the robot swings too wide around the reef, lower it, if it cuts the
        // corner too tight, raise it.
        private const float REEF_AVOID_LEAD_ANGLE_DEGREES = 35f;

        // How far a reef algae piece can drift from where it spawned before algae align treats it as
        // already taken. Nothing in the game-piece framework marks a field piece as removed/scored (see
        // TryGetAlgaeAlignTarget's comment), so this drift check is the only signal available.
        private const float ALGAE_PRESENCE_TOLERANCE_METERS = 0.3f;

        // How close the robot has to get to the farther-back "not ready" standoff before algae align will
        // ever let it pull in to the close standoff. Untested guess, same as every other algae align
        // distance in this file - loosen if the robot never seems to "arrive" and stays stuck on the far
        // standoff, tighten if it pulls in too early.
        private const float ALGAE_FAR_STANDOFF_ARRIVAL_TOLERANCE_METERS = 0.15f;

        // Below this angular separation (degrees, measured around the reef center) between the robot and a
        // reef-adjacent target (algae/processor), ApplyReefAvoidance treats the reef as not being in the way
        // at all and skips routing outright - see its "close-to-reef" early-out comment. Untested guess; if
        // the robot still cuts through the reef approaching a nearby-but-not-quite-same-side face, lower it,
        // if it routes around for faces that were actually a clear direct shot, raise it.
        private const float REEF_AVOID_SAME_SIDE_ANGLE_DEGREES = 90f;

        private ReefscapeRobotBase _stuyBase;
        private DriveController _driveController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;
        private IStuyPulseGamePieceStatus _gamePieceStatus;

        private readonly List<AlignNode> _reefFaces = new();
        private readonly Dictionary<Transform, AlignNode> _reefNodeParents = new();
        private Transform _closestReefNode;
        private Transform _secondClosestReefNode;

        private readonly List<BargeScorer> _bargeScorers = new();
        private readonly List<AlgaeSpot> _algaeSpots = new();

        private Vector3 _blueReefPos;
        private Vector3 _redReefPos;
        private bool _hasReefPos;

        private bool _stationEngaged;
        private float _stationSlide = 0.5f;
        private bool _stationRoutingAroundReef;
        private float _stationRoutingSide;
        private bool _processorRoutingAroundReef;
        private float _processorRoutingSide;
        private bool _algaeRoutingAroundReef;
        private float _algaeRoutingSide;
        private bool _algaeEngaged;
        private bool _algaeReachedFarStandoff;

        private bool _bargeEngaged;
        private float _bargeSlide = 0.5f;
        private bool _bargeRoutingAroundReef;
        private float _bargeRoutingSide;

        private bool _stationAlignActive;
        private bool _bargeAlignActive;
        private bool _processorAlignActive;
        private bool _algaeAlignActive;
        private bool _reefAlignActive;
        private bool _reefAlignLeft;

        private bool _l1Engaged;
        private float _l1Slide;

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

            // Algae pieces are loose "Algae"-tagged GameObjects, not parented under any AlignNode face, so
            // matching each to its nearest face has to be done by proximity here. Low vs High isn't an
            // explicit field on the piece either (Assets/Prefabs/Reefscape/Algae.prefab has no such field),
            // but it IS a fixed property of which face the algae sits on - the real field alternates Low/High
            // around the six faces (confirmed: CD/GH/KL are Low, AB/EF/IJ are High), and the AutoAlignFace
            // GameObjects under each reef's "Nodes" parent are laid out in that same alternating order, so the
            // face's sibling index parity gives the level directly: even (AutoAlignFace, (2), (4)) = Low, odd
            // ((1), (3), (5)) = High.
            var algaePieces = GameObject.FindGameObjectsWithTag("Algae");
            foreach (var piece in algaePieces)
            {
                AlignNode nearestFace = null;
                var nearestDistance = float.MaxValue;
                foreach (var face in _reefFaces)
                {
                    if (face == null) continue;
                    var distance = Vector3.Distance(piece.transform.position, face.transform.position);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestFace = face;
                    }
                }
                if (nearestFace == null) continue;

                // AlignNode/the "ReefFace" tag actually live on the inner "Nodes" child of each
                // AutoAlignFace instance (see Assets/Prefabs/Reefscape/Field/AutoAlignFace.prefab), which is
                // that instance's ONLY child - so face.transform.GetSiblingIndex() is always 0. The sibling
                // index that actually alternates 0-5 around the hexagon belongs to the AutoAlignFace instance
                // itself, one level up.
                _algaeSpots.Add(new AlgaeSpot
                {
                    pieceTransform = piece.transform,
                    spawnPosition = piece.transform.position,
                    isHigh = nearestFace.transform.parent.GetSiblingIndex() % 2 != 0,
                    face = nearestFace
                });
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
            var wasActive = _stationAlignActive || _bargeAlignActive || _processorAlignActive || _algaeAlignActive || _reefAlignActive;

            if (TryGetBargeAlignTarget(out var bargeTarget, out var bargeYaw))
            {
                _bargeAlignActive = true;
                _processorAlignActive = false;
                _algaeAlignActive = false;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(bargeTarget, bargeYaw);
                return;
            }

            _bargeAlignActive = false;

            if (TryGetProcessorAlignTarget(out var processorTarget, out var processorYaw))
            {
                _processorAlignActive = true;
                _algaeAlignActive = false;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(processorTarget, processorYaw);
                return;
            }

            _processorAlignActive = false;

            if (TryGetAlgaeAlignTarget(out var algaeTarget, out var algaeYaw))
            {
                _algaeAlignActive = true;
                _reefAlignActive = false;
                _stationAlignActive = false;
                DriveManualPid(algaeTarget, algaeYaw);
                return;
            }

            _algaeAlignActive = false;

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

            if (_stuyBase == null || _driveController == null) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }

            if (_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed()) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }
            if (_stuyBase.CurrentRobotMode != ReefscapeRobotMode.Coral) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }
            if (_gamePieceStatus != null && _gamePieceStatus.IsIntakingAlgae) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }
            if (_stuyBase.CurrentIntakeMode != ReefscapeIntakeMode.Normal) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }
            if (!_stuyBase.IntakeAction.IsPressed()) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            if (coral != null && coral.HasPiece()) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }

            var zone = GetClosestZone(stationTargets, _stuyBase.Alliance);
            if (zone == null) { _stationEngaged = false; _stationRoutingAroundReef = false; _stationRoutingSide = 0f; return false; }

            if (!TryGetZoneTarget(zone, maxStationAlignDistanceFeet, stationSlideSpeed, 0f, ref _stationEngaged, ref _stationSlide, out targetPosition, "Station"))
            {
                _stationRoutingAroundReef = false;
                _stationRoutingSide = 0f;
                return false;
            }

            targetPosition = ApplyReefAvoidance(targetPosition, ref _stationRoutingAroundReef, ref _stationRoutingSide, "Station");
            targetYaw = zone.yRotation;
            return true;
        }

        // If you're on the far side of the reef from your target, a straight line to it would cut through the
        // reef structure - detect that and redirect through a waypoint that curves around it on whichever
        // side the robot is already leaning toward, instead of cutting the corner. Used by station, barge,
        // and processor align (each keeps its own routing/hysteresis state via the ref params, since only
        // one of them can be driving at a time but their "are we currently routed around" state shouldn't
        // bleed together).
        private Vector3 ApplyReefAvoidance(Vector3 realTarget, ref bool routingAroundReef, ref float routingSide, string debugLabel)
        {
            if (!_hasReefPos)
            {
                return realTarget;
            }

            var reefPos = _stuyBase.Alliance == Alliance.Blue ? _blueReefPos : _redReefPos;
            var robotPos = transform.position;

            var toTarget = realTarget - robotPos;
            toTarget.y = 0f;
            var distanceToTarget = toTarget.magnitude;

            if (distanceToTarget < reefAvoidRadius)
            {
                routingAroundReef = false;
                routingSide = 0f;
                return realTarget;
            }

            // Robot's and the target's angular position around the reef center - computed here (rather than
            // only down in the leading-tangent block) since the close-to-reef check right below also needs it.
            var robotOffset = robotPos - reefPos;
            robotOffset.y = 0f;
            var targetOffset = realTarget - reefPos;
            targetOffset.y = 0f;
            var robotAngleDeg = Mathf.Atan2(robotOffset.z, robotOffset.x) * Mathf.Rad2Deg;
            var targetAngleDeg = Mathf.Atan2(targetOffset.z, targetOffset.x) * Mathf.Rad2Deg;
            var angularSeparationDeg = Mathf.Abs(Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg));

            // Targets that are themselves close to the reef (algae/processor scoring spots) will always show
            // a small clearance on the clamped-projection check below, since the line's closest point to the
            // reef ends up right near the target - that made avoidance falsely engage for the whole approach
            // and only let go once the robot got within reefAvoidRadius of the target (line above). BUT being
            // close to the reef alone doesn't mean the reef isn't in the way - every algae/processor target is
            // close to the reef by design, including ones on the far side of it from the robot. So this only
            // skips avoidance when the robot is also roughly on the same angular side as the target (no reef
            // structure actually between them) - otherwise it falls through to the routing logic below like
            // any other target.
            var targetToReef = reefPos - realTarget;
            targetToReef.y = 0f;
            if (targetToReef.magnitude < reefAvoidRadius && angularSeparationDeg < REEF_AVOID_SAME_SIDE_ANGLE_DEGREES)
            {
                routingAroundReef = false;
                routingSide = 0f;
                return realTarget;
            }

            var lineDir = toTarget / distanceToTarget;
            var toReef = reefPos - robotPos;
            toReef.y = 0f;
            var projection = Mathf.Clamp(Vector3.Dot(toReef, lineDir), 0f, distanceToTarget);
            var closestPointOnLine = robotPos + lineDir * projection;
            var reefClearance = Vector3.Distance(closestPointOnLine, reefPos);

            var exitThreshold = reefAvoidRadius * reefAvoidExitMargin;
            var wasRouting = routingAroundReef;
            var shouldRoute = wasRouting ? reefClearance < exitThreshold : reefClearance < reefAvoidRadius;
            routingAroundReef = shouldRoute;

            if (!shouldRoute)
            {
                routingSide = 0f;
                return realTarget;
            }

            // Confirmed via Play-mode console logs (both barge and processor cases) that a single static
            // waypoint is NOT enough to route around the reef: the robot would drive to the tangent point
            // exactly (distToWaypoint hit 0.00) and then just sit there for 7+ seconds, because the line
            // from THAT waypoint to the real target still clipped inside exitThreshold when the robot and
            // target sit far apart angularly around the reef (~100+ degrees in the reported cases) - one
            // tangent point isn't a path around a circle that wide, it's a dead end. Replaced with a
            // "leading tangent" that sweeps around the reef as the robot moves: aim a fixed angular lead
            // ahead of the robot's own current angular position around reefPos, in a locked rotational
            // direction, so the waypoint keeps advancing (never a fixed point) and the robot genuinely walks
            // around the reef instead of parking on its edge. Rotational direction is locked once at engage
            // (shortest-arc direction from robot's angle to target's angle) so it can't flip mid-route the
            // same way the old left/right "side" pick could. robotAngleDeg/targetAngleDeg already computed
            // above for the same-side early-out check.
            var freshEngage = !wasRouting || routingSide == 0f;
            if (freshEngage)
            {
                var shortestArcDeg = Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg);
                routingSide = shortestArcDeg >= 0f ? 1f : -1f;
            }

            // Console logs (station case) caught this overshooting: robotAngle=116.5, targetAngle=128.1 (only
            // 11.6 degrees apart - target is basically right there), but the unconditional 35-degree lead
            // put the waypoint at 151.5 degrees - 23.4 degrees PAST the target's own angular position. The
            // lead is meant to place a point further along the direction of travel than the target so the
            // path curves around the reef instead of aiming straight at (and through) it, but when the
            // target is already closer than the lead angle, "further than the target" becomes "sweep past
            // it and loop back," which reads exactly like "goes all the way around the reef" for a station
            // that's actually nearby. Clamping the lead to the live angular separation means the waypoint
            // eases onto the target's own bearing instead of overshooting it once the robot gets that close
            // angularly - unaffected for the original far-side case this constant was tuned for (100+
            // degrees apart), where angularSeparationDeg is always well above REEF_AVOID_LEAD_ANGLE_DEGREES.
            var leadAngleDeg = Mathf.Min(REEF_AVOID_LEAD_ANGLE_DEGREES, angularSeparationDeg);
            var waypointAngleDeg = robotAngleDeg + routingSide * leadAngleDeg;
            var waypointAngleRad = waypointAngleDeg * Mathf.Deg2Rad;
            var waypoint = reefPos + new Vector3(Mathf.Cos(waypointAngleRad), 0f, Mathf.Sin(waypointAngleRad)) * reefAvoidRadius;
            waypoint.y = robotPos.y;

            return waypoint;
        }

        // ---- Barge ----

        private bool TryGetBargeAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return false; }

            // No CurrentSetpoint gate here on purpose - holding shooter algae while align is held should
            // always mean "take me to the barge" regardless of whatever setpoint the robot happens to be on
            // at the moment (e.g. still mid-transition from wherever the algae was picked up), not just when
            // CurrentSetpoint already reads Barge. Barge align is checked first in FixedUpdate's priority
            // chain, so this also means it now wins over algae/reef align whenever algae is held and align is
            // pressed, even if CurrentSetpoint isn't Barge - that's the explicit ask, not an oversight. The
            // one exception is Processor: if the driver has deliberately set Processor as the setpoint, that's
            // an explicit "I want to score at the processor" signal that should override the barge default,
            // so processor align (checked right after barge) gets a chance to win instead.
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Processor) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.HasShooterAlgae) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return false; }

            // Hand-placed zones take priority; only fall back to deriving one (from the nearest same-alliance
            // BargeScorer, picking whichever side of it the robot is currently closer to) if nothing
            // hand-placed is in range.
            var zone = GetClosestZone(bargeTargets, _stuyBase.Alliance) ?? DeriveClosestBargeZone();
            if (zone == null) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; return false; }

            if (!TryGetZoneTarget(zone, maxBargeAlignDistanceFeet, bargeSlideSpeed, bargeSlideMidlineGapMeters, ref _bargeEngaged, ref _bargeSlide, out targetPosition, "Barge"))
            {
                _bargeRoutingAroundReef = false;
                _bargeRoutingSide = 0f;
                return false;
            }

            targetPosition = ApplyReefAvoidance(targetPosition, ref _bargeRoutingAroundReef, ref _bargeRoutingSide, "Barge");
            targetYaw = zone.yRotation;
            return true;
        }

        // ---- Processor ----

        private bool TryGetProcessorAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            // Every early-out here also clears the shared reef-avoidance routing state (see the comment on
            // the same pattern in TryGetStationAlignTarget) - without this, releasing the align button and
            // pressing it again moments later reuses a stale locked routingSide/routingAroundReef from the
            // PREVIOUS approach, which can send the robot sweeping the wrong way around the reef on
            // re-engage instead of freshly recomputing for wherever the robot actually is now.
            if (_stuyBase == null || _driveController == null || processorTargets == null || processorTargets.Length == 0) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }
            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.Processor) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }
            if (_gamePieceStatus == null) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }
            if (!_gamePieceStatus.HasShooterAlgae && !_gamePieceStatus.HasFroggyAlgae) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }

            // Same physical spot both times, just a different facing depending on which holder the algae is
            // in: TryReleaseShooterAlgae scores a shooter-held algae by shooting it forward out of the shooter
            // (face the processor, target.yRotation), but a froggy-held algae gets released straight out of
            // froggy instead (FroggyState.AlgaeOuttake) - a different direction, target.froggyAlgaeIntakeYRotation.
            var wantsFroggyIntake = _gamePieceStatus.HasFroggyAlgae;

            // Unlike GetClosestZone (station/barge, unchanged - those aren't what was asked about here), this
            // ignores alliance entirely so a robot can align to either processor, matching the reef/algae fix
            // above: each FixedAlignTarget already carries its own explicit heading, so there's no facing
            // computation that could go wrong the way it did for the reef - picking the nearest regardless of
            // alliance is all that's needed.
            var target = GetClosestFixedTarget(processorTargets);
            if (target == null) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }

            // Which side of the field's centerline the robot is on has to match the target processor's own
            // side - a plain world-X sign comparison instead of a distance cutoff, so align never reaches
            // across the whole field for the wrong-side processor no matter how close that distance happens
            // to read from some far-off spot, and never refuses a genuinely close approach on the near side.
            if ((transform.position.x > 0f) != (target.position.x > 0f)) { _processorRoutingAroundReef = false; _processorRoutingSide = 0f; return false; }

            targetYaw = wantsFroggyIntake ? target.froggyAlgaeIntakeYRotation : target.yRotation;

            var scoringPosition = target.position;
            if (wantsFroggyIntake)
            {
                // The algae isn't necessarily centered in froggy - it rides a slider that can sit left or
                // right. Shift the robot's target the opposite way so the piece itself, not just the robot's
                // nominal center, ends up over the true scoring spot once released. Unlike the reef-branch
                // offsets, this isn't rotated into the target's facing frame - the processor is a single
                // fixed point with a single fixed heading (not a left/right pick per approach side), and the
                // slider itself reads along the world/global X axis, not the robot's local right at whatever
                // yaw froggyAlgaeIntakeYRotation happens to be.
                var sliderOffset = _gamePieceStatus.FroggyAlgaeSliderOffsetMeters;
                scoringPosition += new Vector3(sliderOffset, 0f, 0f);
            }

            // Removing this call entirely (an earlier attempt at fixing a "stuck at a spot away from the
            // processor" report) turned out to be wrong - without it the robot will drive straight through
            // the reef whenever it's genuinely on the far side from the processor target. ApplyReefAvoidance
            // already has a target-close-to-reef early-out (see its own comment) so it won't false-trigger
            // for a target that's basically at the reef; the processor is far enough from reef center that
            // this early-out doesn't apply to it, so the real routing logic runs as intended.
            targetPosition = ApplyReefAvoidance(scoringPosition, ref _processorRoutingAroundReef, ref _processorRoutingSide, "Processor");
            return true;
        }

        private FixedAlignTarget GetClosestFixedTarget(FixedAlignTarget[] targets)
        {
            FixedAlignTarget closest = null;
            var closestDistance = float.MaxValue;
            var robotXZ = new Vector2(transform.position.x, transform.position.z);

            foreach (var target in targets)
            {
                if (target == null) continue;

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

        private bool TryGetZoneTarget(AlignZone zone, float maxDistanceFeet, float slideSpeed, float midlineGapMeters, ref bool engaged, ref float slide, out Vector3 targetPosition, string debugLabel)
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
            var distToCenter = Vector2.Distance(robotXZ, centerXZ);
            if (distToCenter > maxDistanceFeet * FEET_TO_METERS)
            {
                engaged = false;
                return false;
            }

            // If this zone's line actually spans the field midline (x=0 falls strictly between the two
            // corners' x values), find where along the line that crossing sits, expressed as a normalized
            // t. A zone confined to one side of the field never crosses, so this stays null and the margin
            // below is a no-op - the margin isn't a general "stay off the corners" rule, only a "don't
            // linger straddling x=0" one, same motivation as MIDLINE_STOW_BAND_METERS elsewhere in this
            // file but for the slide target instead of the arm/elevator setpoint.
            float? midlineCrossT = null;
            if (zone.leftCorner.x * zone.rightCorner.x < 0f)
            {
                midlineCrossT = zone.leftCorner.x / (zone.leftCorner.x - zone.rightCorner.x);
            }

            var marginT = midlineGapMeters / lineLength;

            // Pushes a slide value that would land within the margin of the midline crossing out to
            // whichever side of the gap it's already closer to, so the target jumps across x=0 instead of
            // parking on top of it.
            float ApplyMidlineGap(float rawSlide)
            {
                if (midlineCrossT is not { } crossT) return rawSlide;
                var lower = crossT - marginT;
                var upper = crossT + marginT;
                if (rawSlide <= lower || rawSlide >= upper) return rawSlide;
                return rawSlide - lower < upper - rawSlide ? lower : upper;
            }

            // Fresh engage (button/context just became true this frame) starts the slide at whichever point
            // on the line is closest to the robot right now, not the middle - so first pressing the button
            // never yanks the target sideways before the driver's stick input takes over.
            if (!engaged)
            {
                var lineVector = zone.rightCorner - zone.leftCorner;
                var t = Vector3.Dot(transform.position - zone.leftCorner, lineVector) / (lineLength * lineLength);
                slide = ApplyMidlineGap(Mathf.Clamp01(t));
            }
            engaged = true;

            var rawStick = _stuyBase.TranslateAction.ReadValue<Vector2>().x;
            var stick = ApplyCameraFlip(rawStick, zone.rightCorner - zone.leftCorner);
            slide = ApplyMidlineGap(Mathf.Clamp01(slide + stick * slideSpeed * Time.fixedDeltaTime / lineLength));

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

        // ---- Algae ----

        // Reef algae pieces are the loose "Algae"-tagged GameObjects matched to their nearest face and
        // Low/High level once in Start() (see _algaeSpots there) - resolving that per-frame would mean
        // redoing the face-proximity search every FixedUpdate for no benefit, since neither a piece's
        // spawn point nor its face assignment ever changes. What DOES need a per-frame check is whether
        // the piece is still on the reef at all: nothing in GamePieceController ever marks a field piece
        // as scored/removed (see ALGAE_PRESENCE_TOLERANCE_METERS's comment), so "still there" is inferred
        // from how far the piece has drifted from where it spawned - once a robot picks one up it moves
        // away immediately, so a small drift tolerance is enough to tell taken from still-on-the-reef.
        private bool TryGetAlgaeAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            // Every early-out here also clears the shared reef-avoidance routing state (same reasoning as
            // TryGetStationAlignTarget/TryGetProcessorAlignTarget) so re-engaging fresh always recomputes
            // routingSide instead of reusing a stale locked value from the previous approach - and also
            // clears _algaeEngaged/_algaeReachedFarStandoff, so a fresh press always starts back at the
            // far "not ready" standoff instead of possibly remembering having reached it last time.
            if (_stuyBase == null || _driveController == null) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }
            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.LowAlgae && _stuyBase.CurrentSetpoint != ReefscapeSetpoints.HighAlgae) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.IsIntakingAlgae) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }

            var wantsHigh = _stuyBase.CurrentSetpoint == ReefscapeSetpoints.HighAlgae;

            AlignNode closestFace = null;
            var closestDistance = float.MaxValue;
            AlignNode closestFaceAnySide = null;
            var closestDistanceAnySide = float.MaxValue;
            var robotOnPositiveSide = transform.position.x >= 0f;

            foreach (var spot in _algaeSpots)
            {
                if (spot.isHigh != wantsHigh) continue;
                if (spot.pieceTransform == null) continue;
                if (Vector3.Distance(spot.pieceTransform.position, spot.spawnPosition) > ALGAE_PRESENCE_TOLERANCE_METERS) continue;

                var distance = Vector3.Distance(transform.position, spot.face.transform.position);
                if (distance < closestDistanceAnySide)
                {
                    closestDistanceAnySide = distance;
                    closestFaceAnySide = spot.face;
                }

                if ((spot.face.transform.position.x >= 0f) != robotOnPositiveSide) continue;

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestFace = spot.face;
                }
            }

            // Prefer a still-available spot on the robot's own side of the field (x>0/x<0) over a closer
            // one across the midline - crossing the midline mid-approach is exactly the "seizure" scenario
            // GetClosestReef()'s flip causes (see IsFacingReef's deadband comment), so favoring same-side
            // spots avoids inducing that crossing in the first place. Falls back to the nearest spot on
            // either side if the robot's own side has nothing left at the wanted level.
            if (closestFace == null)
            {
                closestFace = closestFaceAnySide;
                closestDistance = closestDistanceAnySide;
            }

            if (closestFace == null) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }
            if (closestDistance > maxAlgaeAlignDistanceFeet * FEET_TO_METERS) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeEngaged = false; _algaeReachedFarStandoff = false; return false; }

            // Middle between the face's two coral poles ("the 2 pipes"), standing off along the face's own
            // outward-facing axis - untested which way that axis actually points, so if algaeStandoffInches
            // pulls the robot into the reef instead of away from it, flip its sign. The extra 180 flip here
            // (on top of the existing camera-relative IsFacingReef flip) corrects the base facing, which was
            // backwards - Y-axis rotations commute so it doesn't matter which flip is applied first.
            // Same reasoning as TryAlignToReefNode: facing must be computed against whichever reef this FACE
            // belongs to, not the robot's own alliance reef, so algae align also works on the opposing reef.
            var facingReef = IsFacingReefPos(NearestReefPos(closestFace.transform.position));
            var center = (closestFace.LeftNode.transform.position + closestFace.RightNode.transform.position) * 0.5f;
            var targetRotation = closestFace.transform.rotation * Quaternion.Euler(0, 180, 0);
            if (!facingReef) targetRotation *= Quaternion.Euler(0, 180, 0);

            // Left-right correction, expressed in the robot's own target-facing frame (same idiom as the reef
            // branch AutoAlignOffset) so "positive" consistently means the robot's right regardless of which
            // side algae align approached from - front and back get independent tunables since the mechanism
            // isn't necessarily centered the same way from both directions.
            var lateralOffsetInches = facingReef ? algaeFrontOffsetInches : algaeBackOffsetInches;

            // Routed through ApplyReefAvoidance for consistency with the other align modes - its close-to-reef
            // early-out only skips routing when the robot is angularly on roughly the same side of the reef as
            // the picked face (REEF_AVOID_SAME_SIDE_ANGLE_DEGREES), so picking a face on the far side of the
            // reef (e.g. after the nearest one's algae has already been taken) still routes around it instead
            // of cutting straight through the reef structure.
            // Always visits the farther-back "not ready" standoff first, even if IsAtAlgaeSetpoint already
            // happens to be true the instant align engages (e.g. re-engaging right after a previous algae
            // grab left the superstructure sitting at the setpoint) - only pulls in to the close standoff
            // once the robot has actually arrived at the far standoff at least once THIS engagement AND the
            // superstructure is at setpoint, so the robot always drives the full standoff-then-close path
            // instead of sometimes skipping straight to the close distance.
            var farTarget = center + closestFace.transform.forward * (algaeStandoffNotReadyInches * INCHES_TO_METERS) +
                             targetRotation * new Vector3(lateralOffsetInches * INCHES_TO_METERS, 0f, 0f);
            if (Vector3.Distance(transform.position, farTarget) < ALGAE_FAR_STANDOFF_ARRIVAL_TOLERANCE_METERS)
            {
                _algaeReachedFarStandoff = true;
            }

            var standoffInches = (_algaeReachedFarStandoff && _gamePieceStatus.IsAtAlgaeSetpoint) ? algaeStandoffInches : algaeStandoffNotReadyInches;
            _algaeEngaged = true;
            var rawTarget = center + closestFace.transform.forward * (standoffInches * INCHES_TO_METERS) +
                             targetRotation * new Vector3(lateralOffsetInches * INCHES_TO_METERS, 0f, 0f);
            targetPosition = ApplyReefAvoidance(rawTarget, ref _algaeRoutingAroundReef, ref _algaeRoutingSide, "Algae");
            targetYaw = targetRotation.eulerAngles.y;
            return true;
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
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.LowAlgae || _stuyBase.CurrentSetpoint == ReefscapeSetpoints.HighAlgae) return false;

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

            // Facing is computed against whichever reef this NODE actually belongs to, not the robot's own
            // alliance reef - a robot should be able to align to either reef for coral/algae without issue,
            // but IsFacingReef()/ReefscapeRobotBase.GetFacingReef() is alliance-locked (see _targetReef), so
            // using it here would compute "facing" relative to the wrong, possibly-distant reef whenever the
            // robot targets the opposing alliance's reef - that mismatch is what caused the align rotation to
            // flip 180 degrees when aligning to the opposing reef.
            var facingReef = IsFacingReefPos(NearestReefPos(parent.transform.position));

            var offset = holdingFroggyCoral ? l1offset : GetScoringOffset(wantsLeftSide, facingReef);
            if (offset == null) return false;

            var targetRotation = node.rotation;
            if (!facingReef && !holdingFroggyCoral) targetRotation *= Quaternion.Euler(0, 180, 0);

            // Right after L4, if the driver switches to Algae mode, hold a bigger standoff distance instead of
            // the normal scoring distance - otherwise holding the align button pins the robot close to the
            // reef the whole time and the arm never gets room to reposition for algae.
            var wantsExtraClearance = _gamePieceStatus != null && _gamePieceStatus.WantsExtraReefClearance;
            var zOffset = offset.zOffset + (wantsExtraClearance ? extraReefClearanceInches : 0f);

            var xOffsetInches = offset.xOffset;
            if (holdingFroggyCoral)
            {
                // L1/froggy has no separate left/right offset like the branch scoring does - it's the same
                // l1offset everywhere, so let the driver slide it along the reef face with the translate
                // stick instead. A fresh press of the align button snaps back to the default offset (slide
                // 0); it doesn't recenter just because the stick is released.
                var buttonHeld = _stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed();
                if (!_l1Engaged) _l1Slide = 0f;
                _l1Engaged = buttonHeld;

                var halfRangeInches = l1SlideRangeInches * 0.5f;
                var rawStick = _stuyBase.TranslateAction.ReadValue<Vector2>().x;
                var stick = ApplyCameraFlip(rawStick, targetRotation * Vector3.right);
                _l1Slide = Mathf.Clamp(_l1Slide + stick * l1SlideSpeed * Time.fixedDeltaTime, -halfRangeInches, halfRangeInches);

                // The coral isn't necessarily centered in froggy either - it rides its own slider. Compensate
                // the same way processor align does for the froggy algae slider (StuyPulseAutoAlign's
                // TryGetProcessorAlignTarget), so the piece itself - not just the robot's nominal center -
                // ends up over the true scoring line.
                var coralSliderOffsetInches = _gamePieceStatus.FroggyCoralSliderOffsetMeters / INCHES_TO_METERS;
                xOffsetInches += _l1Slide + coralSliderOffsetInches;
            }

            var localOffset = new Vector3(xOffsetInches, offset.yOffset, zOffset) * INCHES_TO_METERS;
            targetPosition = node.position + targetRotation * localOffset;

            targetRotation *= Quaternion.Euler(0, offset.Rotation, 0);
            targetYaw = targetRotation.eulerAngles.y;

            _reefAlignLeft = wantsLeftSide;
            return true;
        }

        private AutoAlignOffset GetScoringOffset(bool isLeftSide, bool facingReef)
        {
            var isL4 = _stuyBase.CurrentSetpoint == ReefscapeSetpoints.L4;

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

        // Same dot-product idea as ReefscapeRobotBase.CheckFacingReef(), but parameterized by a reef
        // position instead of being locked to the robot's own alliance's reef - needed so reef/algae align
        // can target either reef and still get a correct facing result for the one actually being targeted.
        private bool IsFacingReefPos(Vector3 reefPos)
        {
            var toReefVector = (reefPos - transform.position).normalized;
            return Vector3.Dot(transform.forward, toReefVector) > 0f;
        }

        // Reef branch/algae faces sit right next to their own reef and far from the other one, so picking
        // whichever of the two known reef positions is closer to the face reliably identifies which reef it
        // belongs to without needing a scene hierarchy check.
        private Vector3 NearestReefPos(Vector3 facePos)
        {
            if (!_hasReefPos) return facePos;
            var distToBlue = Vector3.Distance(facePos, _blueReefPos);
            var distToRed = Vector3.Distance(facePos, _redReefPos);
            return distToBlue <= distToRed ? _blueReefPos : _redReefPos;
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

        /// <summary>True while this component is actively driving the robot toward a reef algae spot.</summary>
        public bool AlgaeAlignActive() => _algaeAlignActive;

        /// <summary>
        /// False only in the window between algae align engaging and the robot physically arriving at the
        /// far "not ready" standoff - read by StuyPulseClean/StuyPulseNewArmClean's HandleLowAlgae/HandleHighAlgae
        /// to hold the superstructure at stow during that approach instead of raising into the algae setpoint
        /// early. True (i.e. "go ahead, raise the setpoint") whenever algae align isn't currently engaged at
        /// all, so a manually-picked LowAlgae/HighAlgae setpoint (no align button held) is never blocked.
        /// </summary>
        public bool AlgaeReadyForSetpoint() => !_algaeEngaged || _algaeReachedFarStandoff;

        /// <summary>True while this component is actively driving the robot toward a reef branch.</summary>
        public bool ReefAlignActive() => _reefAlignActive;

        /// <summary>True if the reef branch currently being targeted is the left one.</summary>
        public bool ReefAlignLeft() => _reefAlignLeft;
    }
}
