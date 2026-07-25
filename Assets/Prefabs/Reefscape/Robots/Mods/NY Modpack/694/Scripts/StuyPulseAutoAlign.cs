using System;
using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.Enums;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.PidSystems;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694's single custom auto align - handles reef branch scoring, the barge, and reef algae pickup, the
    /// same way 340's GRRAutoAlign is one self-contained component rather than relying on the shared
    /// framework AutoAlign. It replaces the framework's ReefscapeAutoAlign component for this robot.
    ///
    /// HP station align and processor align were both removed (see the "remove HP station and processor
    /// align entirely" project history) after a build-only bug ("works in Editor, doesn't work in a
    /// standalone Player") in that area couldn't be pinned down without Player-side console/debugger access;
    /// rather than keep guessing at build-only repros, they were dropped rather than fixed. If they're ever
    /// wanted back, the barge/algae/reef-branch code below (ApplyReefAvoidance, GetClosestZone/
    /// TryGetZoneTarget, camera-relative flip) is still the pattern to reuse.
    ///
    /// Barge align works like this: an AlignZone is a corner-to-corner line (its rotation is the heading to
    /// face). Rotation and the distance perpendicular to that line are always PID-corrected, but the
    /// position *along* the line is a slider that continuously tracks whichever point on the line is
    /// closest to the robot as it moves, and can be nudged toward either corner on top of that with the
    /// left stick, clamped so you can't slide past either end - "aligns to whatever's closest, but lets you
    /// slide across it to either corner while it holds you at the right distance away," per the
    /// corresponding real StuyPulse command (github.com/StuyPulse/Aunt-Mary):
    /// SwerveDriveDriveAlignedToBarge118Score (which locks distance-to-line + heading and leaves the
    /// driver's left stick fully in control of position along the line - this is a clamped, centered
    /// version of that same idea rather than the real robot's unclamped open-ended slide).
    ///
    /// Barge align engages automatically while CurrentSetpoint is Barge and the driver is holding algae
    /// ready to score, while either AutoAlignLeft or AutoAlignRight is held - MoSim has no dedicated button
    /// for it, so it reuses the same "hold align" buttons the reef branch align uses, just routed to
    /// different behavior based on what setpoint you're currently in.
    ///
    /// Reef branch align keeps the exact same node-finding and offset-application logic the framework's
    /// ReefscapeAutoAlign used (closest ReefFace-tagged AlignNode, perspective-relative left/right,
    /// the same AutoAlignOffset assets already tuned for this robot) so existing tuned values keep working -
    /// it's just re-hosted here so the whole thing runs through one PID loop instead of two components
    /// fighting over the drivetrain.
    ///
    /// Translation/rotation alignment runs through RobotFramework.Controllers.PidSystems.PIDController, the
    /// same shared controller class ReefscapeAutoAlign (the framework component this replaces) uses - drivePID
    /// powers both the X and Z translate axes (same as ReefscapeAutoAlign's drivePID) and rotatePID powers
    /// rotation, synced from PidConstants each tick the same way ReefscapeAutoAlign's own LateUpdate/UpdatePid
    /// does. Note PIDController's default derivativeMeasurement is Velocity (D term = -rate of change of the
    /// current value, not of error) - same as ReefscapeAutoAlign, since neither sets it explicitly.
    ///
    /// This script doesn't hold its own references to the froggy coral stow / shooter algae stow
    /// GamePieceStates - it reads whether coral/algae is docked there through the sibling robot script's
    /// IStuyPulseGamePieceStatus instead, so that game-piece-system knowledge lives in one place.
    ///
    /// Barge and algae-pickup align both route around the reef instead of cutting through it when the
    /// straight line to the target passes too close to the reef center (see ApplyReefAvoidance) - handles
    /// being on the far side of the reef from wherever you're headed. Each keeps its own independent
    /// routing/hysteresis state so they don't interfere with each other. The corner-to-corner slide on
    /// barge, and the reef branch left/right pick, all account for which way the active camera is facing so
    /// the stick always matches what's visually left/right on screen - same idea as 340's GRRAutoAlign
    /// camera-relative flip, just generalized (see ApplyCameraFlip and CameraFacesNode) instead of copying
    /// its field-axis-specific math.
    ///
    /// Algae align additionally keeps the point where its path crosses the field midline (x=0) at least
    /// algaeMidlineCrossingZMarginMeters onto the robot's own alliance's side of field-center (see
    /// ApplyMidlineCrossingGate) whenever it has to cross to reach a face on the far side (nothing left on
    /// the robot's own side), instead of cutting straight across near mid-field.
    ///
    /// DefaultExecutionOrder(-100) below is load-bearing, not decorative: DriveController.RunSwerve() only
    /// re-reads driver stick input when overideActive is false; while an override is active it just clears
    /// the flag and reuses whatever fwd/str/rotation this script's DriveManualPid -> overideInput last wrote.
    /// That means this script's FixedUpdate MUST run before DriveController's FixedUpdate in the same physics
    /// tick, or DriveController drives on last tick's PID output instead of this tick's - a one-tick lag that
    /// destabilizes the derivative term into visible bounce. Neither script had an explicit execution order
    /// before, so Unity fell back to its default (undefined) ordering, which is free to differ between the
    /// Editor (Mono) and a Player build (IL2Cpp) since it's derived from compiled type order, not declaration
    /// order - this was believed to be the full explanation for "auto align drive PID is smooth in the Editor
    /// but bouncy in a build" (an earlier attempt at this fix incorrectly targeted the chassis Rigidbody's
    /// interpolation setting and was reverted). Kept since it's a real, principled fix for a real one-tick-lag
    /// risk, but it did NOT fully resolve the build-only bounce on its own - a second contributor was a raw,
    /// unfiltered derivative sampled at this project's ~222Hz physics rate, which amplifies tiny
    /// Mono-vs-IL2CPP floating-point noise differences into visible bounce; this component previously worked
    /// around it with a self-contained PID axis (ManualPidAxis) that low-pass filtered the derivative term,
    /// deliberately kept separate from the shared PIDController for that reason. That axis was removed so
    /// this component's PID matches ReefscapeAutoAlign's shared PIDController exactly - if build-only bounce
    /// reappears, the unfiltered derivative here is why; re-adding a low-pass filter is the fix (see git
    /// history for ManualPidAxis's implementation).
    ///
    /// TryAlignToReefNode also holds the robot at the algae standoff point (see GetFaceStandoffTarget) instead
    /// of the normal, much closer L4 scoring offset whenever L4 is selected and the robot is currently closer
    /// than the standoff distance from the align node - purely distance-based, mirrors TryGetAlgaeAlignTarget's
    /// own far-standoff-until-ready pattern, since L4's elevator extension and arm swing sweep through space
    /// the robot would otherwise already be sitting in.
    /// </summary>
    [DefaultExecutionOrder(-100)]
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

        [Header("Reef Avoidance (shared by barge and algae-pickup align)")]
        [Tooltip("If a straight line from the robot to the target would pass this close (meters) to the reef center, route around it instead of cutting through - handles being on the far side of the reef from wherever you're headed.")]
        [SerializeField] private float reefAvoidRadius = 2.5f;

        [Tooltip("Once routing around the reef, require the straight path to clear by this multiple of reefAvoidRadius before switching back to a direct line - avoids flickering between routed/direct right at the boundary.")]
        [SerializeField] private float reefAvoidExitMargin = 1.3f;

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

        [Tooltip("When algae align picks a face on the far side of the field midline from the robot (nothing left on the robot's own side), keeps the point where the path crosses x=0 at least this many meters onto the robot's own alliance's side of field-center (world z > this for Red, world z < -this for Blue), instead of cutting straight across near mid-field.")]
        [SerializeField] private float algaeMidlineCrossingZMarginMeters = 1f;

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

        [Tooltip("Standoff distance (inches) straight out from the align node (the reef branch face being scored), separate from algaeStandoffInches, held while L4 is selected and the robot is currently closer than this distance from the align node (see TryAlignToReefNode's isL4NotReady branch) - purely distance-based, not tied to whether the superstructure has reached L4 yet. L4ReadyForSetpoint() also uses this same distance: the superstructure is allowed to raise to L4 once the robot is at least this far from the align node, not closer. Best-guess default - tune this in Play mode.")]
        [SerializeField] private float l4StandoffInches = 24f;

        [Tooltip("Only assist toward the reef within this distance (feet)")]
        [SerializeField] private float maxReefAlignDistanceFeet = 25f;

        [Tooltip("Extra distance (inches) added to the reef offset's Z when IStuyPulseGamePieceStatus.WantsExtraReefClearance is true (right after L4, switching to Algae) - pushes the align target farther from the reef instead of holding the normal scoring distance. If this ends up pulling the robot closer instead of farther, flip the sign.")]
        [SerializeField] private float extraReefClearanceInches = 24f;

        [Header("Manual PID (RobotFramework.Controllers.PidSystems.PIDController, same as ReefscapeAutoAlign)")]
        [Tooltip("Defaults are carried over from the old ReefscapeAutoAlign component's tuned drivePID (kP 30, kI 0.1, kD 1.65, Max 1, Isaturation 1) - both X and Z use the same drivePID, same as ReefscapeAutoAlign.")]
        [SerializeField] private PidConstants drivePID = new PidConstants(30f, 0.1f, 1.65f, 1f, 1f);

        [Tooltip("Defaults are carried over from the old ReefscapeAutoAlign component's tuned rotatePID (kP 0.1, kI 0, kD 0.003, Max 0.75, Isaturation 1)")]
        [SerializeField] private PidConstants rotatePID = new PidConstants(0.1f, 0f, 0.003f, 0.75f, 1f);

        private PIDController _xPidController;
        private PIDController _zPidController;
        private PIDController _rotatePidController;

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

        // Delay after the algae is confirmed secured before forcing one more retreat to the far standoff -
        // gives the intake motion a moment to settle instead of yanking the target back out the instant
        // HasShooterAlgae/HasFroggyAlgae flips true. Untested guess like the other algae align timings here.
        private const float ALGAE_BACKOFF_DELAY_SECONDS = 0.2f;

        // Below this angular separation (degrees, measured around the reef center) between the robot and a
        // reef-adjacent target (algae/processor), ApplyReefAvoidance treats the reef as not being in the way
        // at all and skips routing outright - see its "close-to-reef" early-out comment. Untested guess; if
        // the robot still cuts through the reef approaching a nearby-but-not-quite-same-side face, lower it,
        // if it routes around for faces that were actually a clear direct shot, raise it.
        private const float REEF_AVOID_SAME_SIDE_ANGLE_DEGREES = 90f;

        // Used by ReefAlignAtTarget() to tell the LED controller when reef/coral align has actually arrived
        // (vs. still driving in) so it can switch from blinking to solid. Untested guesses.
        private const float REEF_ALIGN_POSITION_TOLERANCE_METERS = 0.05f;
        private const float REEF_ALIGN_YAW_TOLERANCE_DEGREES = 3f;

        private ReefscapeRobotBase _stuyBase;
        private DriveController _driveController;
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

        private bool _algaeRoutingAroundReef;
        private float _algaeRoutingSide;
        // Which reef the current avoidance sweep is routed around (true = blue, false = red, null = not
        // routing). Tracked separately from _algaeRoutingAroundReef so a change in the nearest-reef pick
        // (as the robot physically crosses the field) can force a fresh engage instead of letting
        // ApplyCircularAvoidance's locked routingSide/angle math keep applying to the wrong obstacle.
        private bool? _algaeRoutingObstacleIsBlue;
        private bool _algaeMidlineGateActive;
        private bool _algaeEngaged;
        private bool _algaeReachedFarStandoff;
        private AlignNode _algaeTargetFace;
        private float? _algaeSecuredSince;
        private bool _algaeBackoffApplied;
        private bool _algaeBackoffComplete;

        private bool _bargeEngaged;

        // Offset-from-live-closest-point semantics - see TryGetZoneTarget.
        private float _bargeSlide;
        // Frozen the instant _bargeSlide becomes nonzero - see TryGetZoneTarget.
        private float _bargeSlideBaseline;
        private bool _bargeRoutingAroundReef;
        private float _bargeRoutingSide;
        // Same tracking idiom as _algaeRoutingObstacleIsBlue - barge align now routes around whichever
        // reef is nearest the robot's own live position (see TryGetBargeAlignTarget), since the robot can
        // legitimately be on the opposing reef's side when barge align takes over right after an algae
        // pickup there, and that selection can flip mid-route.
        private bool? _bargeRoutingObstacleIsBlue;

        private bool _bargeAlignActive;
        private bool _algaeAlignActive;
        private bool _reefAlignActive;
        private bool _reefAlignLeft;
        private Vector3 _reefAlignTargetPosition;
        private float _reefAlignTargetYaw;
        // Tracks the isL4NotReady standoff hold (see TryAlignToReefNode) separately from algae align's own
        // _algaeEngaged/_algaeReachedFarStandoff - L4ReadyForSetpoint() must measure distance to the align
        // node being scored, not the unrelated reef algae node/spot algae align tracks.
        private bool _l4Engaged;
        private bool _l4ReachedStandoff;

        private bool _l1Engaged;
        private float _l1Slide;

        private void Awake()
        {
            _stuyBase = GetComponent<ReefscapeRobotBase>();
            _driveController = GetComponent<DriveController>();
            _gamePieceStatus = GetComponent<IStuyPulseGamePieceStatus>();

            _xPidController = new PIDController();
            _zPidController = new PIDController();
            _rotatePidController = new PIDController();
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
                // GameObject.FindGameObjectsWithTag("Algae") also picks up the loose "lollipop" algae pieces
                // nested under Assets/Prefabs/Reefscape/Field/GamePieceWorld.prefab (a floor/ground-intake
                // stash, not reef-mounted) - those never move, so the drift-based "has it been taken" check
                // below never excludes them, letting one (e.g. "Algae (3)") permanently read as an available
                // High algae target on every reef even once the real reef algae is cleared. Skip anything
                // parented under the GamePieceWorld root so only the genuine reef-mounted pieces are matched.
                if (piece.transform.parent != null && piece.transform.parent.CompareTag("GamePieceWorld")) continue;

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
            var wasActive = _bargeAlignActive || _algaeAlignActive || _reefAlignActive;

            if (TryGetBargeAlignTarget(out var bargeTarget, out var bargeYaw))
            {
                _bargeAlignActive = true;
                _algaeAlignActive = false;
                _reefAlignActive = false;
                _l4Engaged = false;
                _l4ReachedStandoff = false;
                DriveManualPid(bargeTarget, bargeYaw);
                return;
            }

            _bargeAlignActive = false;

            if (TryGetAlgaeAlignTarget(out var algaeTarget, out var algaeYaw))
            {
                _algaeAlignActive = true;
                _reefAlignActive = false;
                _l4Engaged = false;
                _l4ReachedStandoff = false;
                DriveManualPid(algaeTarget, algaeYaw);
                return;
            }

            _algaeAlignActive = false;

            if (TryGetReefAlignTarget(out var reefTarget, out var reefYaw))
            {
                _reefAlignActive = true;
                DriveManualPid(reefTarget, reefYaw);
                return;
            }

            _reefAlignActive = false;
            _l4Engaged = false;
            _l4ReachedStandoff = false;

            if (wasActive) ResetPid();
        }

        // If you're on the far side of an obstacle circle (the reef) from your target, a straight line to it
        // would cut through/too close to that obstacle - detect that and redirect through a waypoint that
        // curves around it on whichever side the robot is already leaning toward, instead of cutting the
        // corner. Generic in obstacle position/radius so any future caller besides ApplyReefAvoidance
        // (barge/algae vs. the reef) can reuse it too, each keeping its own routing/hysteresis state via
        // the ref params so simultaneous callers' "currently routed around" state doesn't bleed together.
        //
        // A single static tangent point is NOT enough to route around a circle: confirmed via Play-mode
        // console logs (originally against the reef, both barge and (now-removed) processor align cases) that the robot would
        // drive to the tangent point exactly (distToWaypoint hit 0.00) and then just sit there for 7+ seconds,
        // because the line from THAT waypoint to the real target still clipped inside exitThreshold when the
        // robot and target sit far apart angularly around the obstacle - one tangent point isn't a path
        // around a circle, it's a dead end. This is a "leading tangent" that sweeps around the obstacle as the
        // robot moves instead: aim a fixed angular lead ahead of the robot's own current angular position
        // around obstaclePos, in a locked rotational direction, so the waypoint keeps advancing (never a fixed
        // point) and the robot genuinely walks around the obstacle instead of parking on its edge. Rotational
        // direction is locked once at engage (shortest-arc direction from robot's angle to target's angle) so
        // it can't flip mid-route. This same class of "waypoint recomputed from the current straight line
        // freezes into a self-consistent dead end" bug reappeared for the midline-slider-clearance case when
        // it was first written as a simple one-shot perpendicular push instead of going through this shared
        // sweep - don't reintroduce a non-swept version for a new caller no matter how small avoidRadius is.
        private Vector3 ApplyCircularAvoidance(Vector3 realTarget, Vector3 obstaclePos, float avoidRadius, float exitMargin, float sameSideSkipAngleDeg, float leadAngleDegMax, ref bool routingAround, ref float routingSide)
        {
            var robotPos = transform.position;

            var toTarget = realTarget - robotPos;
            toTarget.y = 0f;
            var distanceToTarget = toTarget.magnitude;

            if (distanceToTarget < avoidRadius)
            {
                routingAround = false;
                routingSide = 0f;
                return realTarget;
            }

            // Robot's and the target's angular position around the obstacle center - computed here (rather
            // than only down in the leading-tangent block) since the close-to-obstacle check right below also
            // needs it.
            var robotOffset = robotPos - obstaclePos;
            robotOffset.y = 0f;
            var targetOffset = realTarget - obstaclePos;
            targetOffset.y = 0f;
            var robotAngleDeg = Mathf.Atan2(robotOffset.z, robotOffset.x) * Mathf.Rad2Deg;
            var targetAngleDeg = Mathf.Atan2(targetOffset.z, targetOffset.x) * Mathf.Rad2Deg;
            var angularSeparationDeg = Mathf.Abs(Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg));

            // Targets that are themselves close to the obstacle (e.g. algae/processor scoring spots vs. the
            // reef) will always show a small clearance on the clamped-projection check below, since the
            // line's closest point to the obstacle ends up right near the target - that made avoidance
            // falsely engage for the whole approach and only let go once the robot got within avoidRadius of
            // the target (line above). BUT being close to the obstacle alone doesn't mean it isn't in the way
            // - such a target is close to the obstacle by design, including when it's on the far side of the
            // obstacle from the robot. So this only skips avoidance when the robot is also roughly on the
            // same angular side as the target (no obstacle actually between them) - otherwise it falls
            // through to the routing logic below like any other target.
            var targetToObstacle = obstaclePos - realTarget;
            targetToObstacle.y = 0f;
            if (targetToObstacle.magnitude < avoidRadius && angularSeparationDeg < sameSideSkipAngleDeg)
            {
                routingAround = false;
                routingSide = 0f;
                return realTarget;
            }

            var lineDir = toTarget / distanceToTarget;
            var toObstacle = obstaclePos - robotPos;
            toObstacle.y = 0f;
            var projection = Mathf.Clamp(Vector3.Dot(toObstacle, lineDir), 0f, distanceToTarget);
            var closestPointOnLine = robotPos + lineDir * projection;
            var obstacleClearance = Vector3.Distance(closestPointOnLine, obstaclePos);

            var exitThreshold = avoidRadius * exitMargin;
            var wasRouting = routingAround;
            var shouldRoute = wasRouting ? obstacleClearance < exitThreshold : obstacleClearance < avoidRadius;
            routingAround = shouldRoute;

            if (!shouldRoute)
            {
                routingSide = 0f;
                return realTarget;
            }

            var freshEngage = !wasRouting || routingSide == 0f;
            if (freshEngage)
            {
                var shortestArcDeg = Mathf.DeltaAngle(robotAngleDeg, targetAngleDeg);
                routingSide = shortestArcDeg >= 0f ? 1f : -1f;
            }

            // Console logs (station case, against the reef) caught this overshooting: robotAngle=116.5,
            // targetAngle=128.1 (only 11.6 degrees apart - target is basically right there), but the
            // unconditional 35-degree lead put the waypoint at 151.5 degrees - 23.4 degrees PAST the target's
            // own angular position. The lead is meant to place a point further along the direction of travel
            // than the target so the path curves around the obstacle instead of aiming straight at (and
            // through) it, but when the target is already closer than the lead angle, "further than the
            // target" becomes "sweep past it and loop back," which reads exactly like "goes all the way
            // around the obstacle" for a target that's actually nearby. Clamping the lead to the live angular
            // separation means the waypoint eases onto the target's own bearing instead of overshooting it
            // once the robot gets that close angularly - unaffected for the original far-side reef case this
            // was tuned for (100+ degrees apart), where angularSeparationDeg is always well above the lead cap.
            var leadAngleDeg = Mathf.Min(leadAngleDegMax, angularSeparationDeg);
            var waypointAngleDeg = robotAngleDeg + routingSide * leadAngleDeg;
            var waypointAngleRad = waypointAngleDeg * Mathf.Deg2Rad;
            var waypoint = obstaclePos + new Vector3(Mathf.Cos(waypointAngleRad), 0f, Mathf.Sin(waypointAngleRad)) * avoidRadius;
            waypoint.y = robotPos.y;

            return waypoint;
        }

        // reefPosOverride lets a caller route around whichever reef is actually near the ROBOT right now
        // instead of always the robot's own alliance reef - needed by both algae align (can legitimately
        // target a face on the opposing reef) and barge align (can be handed off to right after an algae
        // pickup on the opposing reef, so the robot may still be sitting on that side). Without this, a
        // robot crossing to/from the far reef had its avoidance obstacle pinned to its own (often
        // irrelevant, far-away) reef, so the actual nearby reef structure it was crossing past was never
        // routed around at all. Both current callers pass NearestReefPos(transform.position) - see their
        // own comments for why "nearest to the target" was tried first and found to be a no-op for the
        // common case (target on the robot's own alliance's reef).
        private Vector3 ApplyReefAvoidance(Vector3 realTarget, ref bool routingAroundReef, ref float routingSide, string debugLabel, Vector3? reefPosOverride = null)
        {
            if (!_hasReefPos) return realTarget;

            var reefPos = reefPosOverride ?? (_stuyBase.Alliance == Alliance.Blue ? _blueReefPos : _redReefPos);
            return ApplyCircularAvoidance(realTarget, reefPos, reefAvoidRadius, reefAvoidExitMargin, REEF_AVOID_SAME_SIDE_ANGLE_DEGREES, REEF_AVOID_LEAD_ANGLE_DEGREES, ref routingAroundReef, ref routingSide);
        }

        // Called only for an algae approach that crosses the field midline (the picked face is on the
        // opposite side from the robot). Replaced two prior attempts at this (a plain perpendicular push off
        // the barge line's near corner, then a leading-tangent sweep around that corner via
        // ApplyCircularAvoidance) after both produced dead-end/wrap-around-the-reef symptoms - the actual ask
        // turned out to be much simpler and not about the barge line's geometry at all: keep the crossing
        // point itself (where the path crosses x=0) at least algaeMidlineCrossingZMarginMeters onto the
        // robot's own alliance's side of field-center, full stop. Computes where the straight line from the
        // robot to realTarget would cross x=0 and, if that crossing z violates the alliance's side (z <
        // margin for Red, z > -margin for Blue), redirects through a waypoint that's still realTarget's own
        // x/y (only z is overridden) instead. Keeping the real target's x means the PID never has to brake
        // to zero velocity at a literal x=0 stop - only the earlier version, which zeroed x too, did that,
        // producing a visible stop-and-resume right as the robot crossed midline. Once redirected, stays
        // locked onto this gate (_algaeMidlineGateActive) until crossingMidline itself goes false (the robot
        // has actually reached the target's side) rather than re-deciding every tick - re-deciding from the
        // live robot->target line each tick is exactly what made both earlier attempts unstable, since the
        // crossing z it produces isn't monotonic as the robot approaches a waypoint that isn't the real target.
        private Vector3 ApplyMidlineCrossingGate(Vector3 realTarget, bool crossingMidline)
        {
            if (!crossingMidline || _stuyBase == null)
            {
                _algaeMidlineGateActive = false;
                return realTarget;
            }

            var robotPos = transform.position;
            var dx = realTarget.x - robotPos.x;
            if (Mathf.Abs(dx) < MIN_LINE_LENGTH)
            {
                _algaeMidlineGateActive = false;
                return realTarget;
            }

            var isRed = _stuyBase.Alliance == Alliance.Red;
            var requiredZ = isRed ? algaeMidlineCrossingZMarginMeters : -algaeMidlineCrossingZMarginMeters;

            var t = Mathf.Clamp01(-robotPos.x / dx);
            var crossZ = robotPos.z + t * (realTarget.z - robotPos.z);
            var violated = isRed ? crossZ < requiredZ : crossZ > requiredZ;

            _algaeMidlineGateActive = _algaeMidlineGateActive || violated;
            if (!_algaeMidlineGateActive) return realTarget;

            return new Vector3(realTarget.x, realTarget.y, requiredZ);
        }

        // ---- Barge ----

        private bool TryGetBargeAlignTarget(out Vector3 targetPosition, out float targetYaw)
        {
            targetPosition = Vector3.zero;
            targetYaw = 0f;

            if (_stuyBase == null || _driveController == null) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; _bargeRoutingObstacleIsBlue = null; return false; }

            // No CurrentSetpoint gate here on purpose - holding shooter algae while align is held should
            // always mean "take me to the barge" regardless of whatever setpoint the robot happens to be on
            // at the moment (e.g. still mid-transition from wherever the algae was picked up), not just when
            // CurrentSetpoint already reads Barge. Barge align is checked first in FixedUpdate's priority
            // chain, so this also means it now wins over algae/reef align whenever algae is held and align is
            // pressed, even if CurrentSetpoint isn't Barge - that's the explicit ask, not an oversight. The
            // one exception is Processor: if the driver has deliberately set Processor as the setpoint, that's
            // an explicit "I want to score at the processor" signal, so exclude barge rather than yanking the
            // robot toward the barge instead (processor align itself was removed - see the class doc comment -
            // but this exclusion is kept so setting Processor doesn't get overridden by barge). Same idea for
            // LowAlgae/HighAlgae: the instant a held algae secures mid-pickup, HasShooterAlgae flips true and
            // this method would otherwise yank the robot straight toward the barge before algae align's own
            // "back off to the far standoff first" retreat (see TryGetAlgaeAlignTarget) ever gets a chance to
            // run, since barge is checked earlier in FixedUpdate's priority chain - so while still in an algae
            // setpoint, exclude barge only until that retreat finishes (_algaeBackoffComplete); once the robot
            // has backed off to the far standoff, hand off to barge align as normal.
            if (_stuyBase.CurrentSetpoint == ReefscapeSetpoints.Processor ||
                ((_stuyBase.CurrentSetpoint == ReefscapeSetpoints.LowAlgae || _stuyBase.CurrentSetpoint == ReefscapeSetpoints.HighAlgae) && !_algaeBackoffComplete))
            { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; _bargeRoutingObstacleIsBlue = null; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; _bargeRoutingObstacleIsBlue = null; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.HasShooterAlgae) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; _bargeRoutingObstacleIsBlue = null; return false; }

            // Hand-placed zones take priority; only fall back to deriving one (from the nearest same-alliance
            // BargeScorer, picking whichever side of it the robot is currently closer to) if nothing
            // hand-placed is in range.
            var zone = GetClosestZone(bargeTargets, _stuyBase.Alliance) ?? DeriveClosestBargeZone();
            if (zone == null) { _bargeEngaged = false; _bargeRoutingAroundReef = false; _bargeRoutingSide = 0f; _bargeRoutingObstacleIsBlue = null; return false; }

            if (!TryGetZoneTarget(zone, maxBargeAlignDistanceFeet, bargeSlideSpeed, bargeSlideMidlineGapMeters, ref _bargeEngaged, ref _bargeSlide, ref _bargeSlideBaseline, out targetPosition, "Barge"))
            {
                _bargeRoutingAroundReef = false;
                _bargeRoutingSide = 0f;
                _bargeRoutingObstacleIsBlue = null;
                return false;
            }

            // Route around whichever reef is actually near the ROBOT right now, not always the robot's own
            // alliance reef - barge align takes over right after an algae pickup on the opposing reef (see
            // the comment above on the LowAlgae/HighAlgae exclusion), so the robot can genuinely be sitting
            // on the far side when this first engages. Same fix and same fresh-engage-on-change handling as
            // TryGetAlgaeAlignTarget's identical reef-selection bug - see that method's comments for why the
            // naive alliance-based pick silently does nothing for the common "grab from your own alliance's
            // reef" case but matters for this cross-field case.
            var routingObstaclePos = NearestReefPos(transform.position);
            var routingObstacleIsBlue = _hasReefPos ? (bool?)(routingObstaclePos == _blueReefPos) : null;
            if (_bargeRoutingAroundReef && _bargeRoutingObstacleIsBlue.HasValue && routingObstacleIsBlue.HasValue &&
                _bargeRoutingObstacleIsBlue.Value != routingObstacleIsBlue.Value)
            {
                _bargeRoutingAroundReef = false;
                _bargeRoutingSide = 0f;
            }
            _bargeRoutingObstacleIsBlue = routingObstacleIsBlue;
            targetPosition = ApplyReefAvoidance(targetPosition, ref _bargeRoutingAroundReef, ref _bargeRoutingSide, "Barge", routingObstaclePos);
            targetYaw = zone.yRotation;
            return true;
        }

        // ---- Shared corner-to-corner slide logic used by barge align ----

        private bool TryGetZoneTarget(AlignZone zone, float maxDistanceFeet, float slideSpeed, float midlineGapMeters, ref bool engaged, ref float slide, ref float slideBaseline, out Vector3 targetPosition, string debugLabel)
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

            // The point on the line closest to the robot right now - recomputed every frame (not just at
            // fresh engage) so the baseline keeps tracking the robot as it moves during the approach, instead
            // of freezing wherever it happened to be when the align button was first pressed.
            var lineVector = zone.rightCorner - zone.leftCorner;
            var closestT = Vector3.Dot(transform.position - zone.leftCorner, lineVector) / (lineLength * lineLength);

            // Fresh engage (button/context just became true this frame) resets the stick offset to zero, so
            // first pressing the button starts exactly on the live closest point, not wherever a stale
            // offset from a previous engagement would have left it.
            if (!engaged) slide = 0f;
            engaged = true;

            // slideBaseline only re-locks onto the robot's own live closest point while the driver hasn't
            // nudged the stick yet this engagement (slide == 0f) - the moment any stick input accumulates a
            // nonzero offset, the baseline freezes for the rest of this engagement instead of continuing to
            // chase closestT. This used to just add `slide` on top of a live `closestT` every frame - but the
            // robot's own position moves TOWARD whatever target that produces, so closestT (the robot's own
            // projection) converges toward (baseline + slide), and re-adding the same slide on top of that
            // already-shifted baseline pushed the target further in the same direction every tick - a single
            // stick tap never settled, it ran all the way to whichever end of the slider the tap faced
            // (reported: "if i tap left or right itll keep going left or right til the end of the slider").
            // Freezing the baseline once sliding starts breaks that feedback loop while still preserving the
            // "track the robot on approach" behavior for as long as the driver leaves the stick alone.
            if (slide == 0f) slideBaseline = closestT;

            var rawStick = _stuyBase.TranslateAction.ReadValue<Vector2>().x;
            var stick = ApplyCameraFlip(rawStick, lineVector);
            slide += stick * slideSpeed * Time.fixedDeltaTime / lineLength;

            var finalT = ApplyMidlineGap(Mathf.Clamp01(slideBaseline + slide));
            targetPosition = Vector3.Lerp(zone.leftCorner, zone.rightCorner, finalT);
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
            // TryGetBargeAlignTarget) so re-engaging fresh always recomputes
            // routingSide instead of reusing a stale locked value from the previous approach - and also
            // clears _algaeEngaged/_algaeReachedFarStandoff, so a fresh press always starts back at the
            // far "not ready" standoff instead of possibly remembering having reached it last time.
            if (_stuyBase == null || _driveController == null) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }
            if (_stuyBase.CurrentSetpoint != ReefscapeSetpoints.LowAlgae && _stuyBase.CurrentSetpoint != ReefscapeSetpoints.HighAlgae) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }
            if (!(_stuyBase.AutoAlignLeftAction.IsPressed() || _stuyBase.AutoAlignRightAction.IsPressed())) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }
            if (_gamePieceStatus == null || !_gamePieceStatus.IsIntakingAlgae) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }

            var wantsHigh = _stuyBase.CurrentSetpoint == ReefscapeSetpoints.HighAlgae;

            // Once the algae is actually secured, the piece being held is by definition the one that was
            // just taken from _algaeTargetFace - re-running the nearest-available-spot search at this point
            // would immediately exclude that face (its piece has moved away from spawn, see
            // ALGAE_PRESENCE_TOLERANCE_METERS) and jump the target straight to the NEXT closest face's far
            // standoff instead of just backing straight away from where the robot already is. So while secured,
            // skip the search entirely and keep targeting the same face that was locked in before pickup - the
            // face search only resumes once the piece is released/scored and a fresh engage starts over.
            var isSecured = _gamePieceStatus.HasShooterAlgae || _gamePieceStatus.HasFroggyAlgae;

            AlignNode closestFace = null;
            var closestDistance = 0f;

            if (isSecured && _algaeTargetFace != null)
            {
                closestFace = _algaeTargetFace;
                closestDistance = Vector3.Distance(transform.position, closestFace.transform.position);
            }
            else
            {
                closestDistance = float.MaxValue;
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
            }

            if (closestFace == null) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }
            if (closestDistance > maxAlgaeAlignDistanceFeet * FEET_TO_METERS) { _algaeRoutingAroundReef = false; _algaeRoutingSide = 0f; _algaeRoutingObstacleIsBlue = null; _algaeMidlineGateActive = false; _algaeEngaged = false; _algaeReachedFarStandoff = false; _algaeTargetFace = null; _algaeSecuredSince = null; _algaeBackoffApplied = false; _algaeBackoffComplete = false; return false; }

            // Used below (after ApplyReefAvoidance) to gate ApplyMidlineCrossingGate - only relevant when
            // this approach is actually crossing from the robot's own side of the field to the picked face's
            // side, i.e. exactly the closestFaceAnySide fallback case above.
            var crossingMidline = (closestFace.transform.position.x >= 0f) != (transform.position.x >= 0f);

            // If the picked face changed since last frame (e.g. this one's algae just got taken, or the
            // robot drifted enough that a different face is now closest), treat it like a fresh engage for
            // standoff purposes - forces another visit to the far "not ready" standoff before pulling back in
            // close, so switching faces backs the robot away from the reef first instead of sliding directly
            // from one face's close standoff to another's. Never actually fires while isSecured (closestFace
            // is pinned to _algaeTargetFace above), only relevant during the pre-pickup approach.
            if (closestFace != _algaeTargetFace)
            {
                _algaeReachedFarStandoff = false;
                _algaeTargetFace = closestFace;
            }

            // Once the algae is actually secured (not just mid-intake), force one more retreat to the far
            // "not ready" standoff before letting the robot slide left/right onto whatever face is picked
            // next - otherwise a held algae piece can clip the reef structure while the robot swings laterally
            // close in. Waits ALGAE_BACKOFF_DELAY_SECONDS after securing (rather than triggering the instant
            // HasShooterAlgae/HasFroggyAlgae flips true) so the intake motion has a moment to settle before
            // the align target yanks back outward. _algaeBackoffApplied makes this a one-shot per pickup - it
            // doesn't keep forcing the far standoff every frame the algae stays held, just once right after.
            if (!isSecured)
            {
                _algaeSecuredSince = null;
                _algaeBackoffApplied = false;
                _algaeBackoffComplete = false;
            }
            else
            {
                _algaeSecuredSince ??= Time.time;
                if (!_algaeBackoffApplied && Time.time - _algaeSecuredSince.Value >= ALGAE_BACKOFF_DELAY_SECONDS)
                {
                    _algaeReachedFarStandoff = false;
                    _algaeBackoffApplied = true;
                }
            }

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
            var atFarStandoff = Vector3.Distance(transform.position, farTarget) < ALGAE_FAR_STANDOFF_ARRIVAL_TOLERANCE_METERS;
            if (atFarStandoff)
            {
                _algaeReachedFarStandoff = true;
                // Once the post-pickup backoff has kicked in, arriving at the far standoff means the retreat
                // is done - flag it so TryGetBargeAlignTarget (checked earlier in FixedUpdate's priority
                // chain) is allowed to take over instead of algae align continuing to hold this spot.
                if (_algaeBackoffApplied) _algaeBackoffComplete = true;
            }

            // While the post-pickup backoff is in effect, stay pinned to the far standoff regardless of
            // _algaeReachedFarStandoff/IsAtAlgaeSetpoint - without this, the instant the robot arrives at the
            // far standoff the line above sets _algaeReachedFarStandoff back to true and the ordinary
            // "pull in once ready" rule below would immediately pull it back into the close standoff, which
            // is exactly the "backs out then back into the algae align" oscillation that was reported.
            var standoffInches = _algaeBackoffApplied
                ? algaeStandoffNotReadyInches
                : (_algaeReachedFarStandoff && _gamePieceStatus.IsAtAlgaeSetpoint) ? algaeStandoffInches : algaeStandoffNotReadyInches;
            _algaeEngaged = true;
            var rawTarget = center + closestFace.transform.forward * (standoffInches * INCHES_TO_METERS) +
                             targetRotation * new Vector3(lateralOffsetInches * INCHES_TO_METERS, 0f, 0f);
            // Midline gate has to run BEFORE reef avoidance, against rawTarget (the actual destination), not
            // after - ApplyReefAvoidance's leading-tangent waypoints (see ApplyCircularAvoidance) are, by
            // design, always close to the robot's own current position while actively routing, so feeding
            // those into the gate made its dx (waypoint.x - robotPos.x) frequently fall under MIN_LINE_LENGTH,
            // silently skipping the z-margin check entirely for most of an active reef-avoidance sweep. Gating
            // the real destination first, then routing avoidance toward the (already alliance-safe) result,
            // means the constraint can't be bypassed by the avoidance sweep's own intermediate waypoints.
            var gatedTarget = ApplyMidlineCrossingGate(rawTarget, crossingMidline);
            // Route around whichever reef is actually near the ROBOT right now, not the reef the target
            // face happens to be mounted on - NearestReefPos(closestFace...) coincidentally reproduces the
            // old alliance-based obstacle whenever the target sits on the robot's own alliance's reef
            // (e.g. crossing back from the opposing side to collect more of your own algae), which is the
            // common case, so that version of the fix never actually changed anything for it. The reef
            // that's physically in the way during a crossing is the one nearest the robot's own live
            // position, which does correctly differ from the alliance-based pick while mid-crossing.
            var routingObstaclePos = NearestReefPos(transform.position);
            var routingObstacleIsBlue = _hasReefPos ? (bool?)(routingObstaclePos == _blueReefPos) : null;
            if (_algaeRoutingAroundReef && _algaeRoutingObstacleIsBlue.HasValue && routingObstacleIsBlue.HasValue &&
                _algaeRoutingObstacleIsBlue.Value != routingObstacleIsBlue.Value)
            {
                // The nearest reef flipped out from under an in-progress avoidance sweep (the robot crossed
                // the field's midpoint mid-route) - force a fresh engage so ApplyCircularAvoidance's locked
                // routingSide/angle math doesn't keep applying to the reef we were routing around a moment
                // ago instead of the one actually in front of the robot now.
                _algaeRoutingAroundReef = false;
                _algaeRoutingSide = 0f;
            }
            _algaeRoutingObstacleIsBlue = routingObstacleIsBlue;
            targetPosition = ApplyReefAvoidance(gatedTarget, ref _algaeRoutingAroundReef, ref _algaeRoutingSide, "Algae", routingObstaclePos);
            targetYaw = targetRotation.eulerAngles.y;
            return true;
        }

        // Computes the same "middle between the face's 2 pipes, standing off along the face's own
        // outward-facing axis" point TryGetAlgaeAlignTarget uses above, parameterized by which face/distance
        // so TryAlignToReefNode can send the robot to this exact point (the "algae standoff align point")
        // while L4 is still climbing to setpoint - see its isL4 branch below for why. Left as its own helper
        // rather than folding into TryGetAlgaeAlignTarget itself, since that method also has to handle far-
        // standoff/backoff/routing state this L4 use case doesn't need.
        private void GetFaceStandoffTarget(AlignNode face, float standoffInches, bool facingReef, out Vector3 targetPosition, out float targetYaw)
        {
            var center = (face.LeftNode.transform.position + face.RightNode.transform.position) * 0.5f;
            var targetRotation = face.transform.rotation * Quaternion.Euler(0, 180, 0);
            if (!facingReef) targetRotation *= Quaternion.Euler(0, 180, 0);
            var lateralOffsetInches = facingReef ? algaeFrontOffsetInches : algaeBackOffsetInches;
            targetPosition = center + face.transform.forward * (standoffInches * INCHES_TO_METERS) +
                              targetRotation * new Vector3(lateralOffsetInches * INCHES_TO_METERS, 0f, 0f);
            targetYaw = targetRotation.eulerAngles.y;
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

            // While L4 is selected, hold at the same standoff point algae align uses instead of pulling in to
            // the normal (much closer) L4 scoring offset below, until the robot has reached the standoff
            // distance from the align node at least once - L4's elevator extension and arm swing sweep
            // through space the robot would otherwise already be sitting in, so parking close to the reef
            // risks clipping it. This is purely distance-based, not tied to whether the superstructure has
            // actually reached L4 yet (IStuyPulseGamePieceStatus.IsAtL4Setpoint). _l4ReachedStandoff is a
            // one-way latch rather than a live per-frame distance check: the ordinary scoring offset sits
            // closer to the node than the standoff distance, so re-checking "am I closer than standoff"
            // every frame would flip back and forth right at the boundary - the moment the robot drives
            // past the standoff toward the closer offset, it re-qualifies as "closer than standoff" and
            // gets redirected straight back out, forever oscillating in place instead of ever reaching the
            // real target. Latching means once the robot has reached the standoff distance, it commits to
            // driving all the way in to the normal offset-based target the same as every other level. Only
            // applies to the real L4 branch offsets, not holdingFroggyCoral's l1offset (L1 has no such
            // transition concern).
            var isCloserThanL4Standoff = Vector3.Distance(transform.position, node.position) <
                                          l4StandoffInches * INCHES_TO_METERS;
            if (!holdingFroggyCoral && _stuyBase.CurrentSetpoint == ReefscapeSetpoints.L4)
            {
                _l4Engaged = true;
                if (!isCloserThanL4Standoff) _l4ReachedStandoff = true;
            }

            var isL4NotReady = !holdingFroggyCoral && _stuyBase.CurrentSetpoint == ReefscapeSetpoints.L4 &&
                                !_l4ReachedStandoff;
            if (isL4NotReady)
            {
                GetFaceStandoffTarget(parent, l4StandoffInches, facingReef, out targetPosition, out targetYaw);

                _reefAlignLeft = wantsLeftSide;
                _reefAlignTargetPosition = targetPosition;
                _reefAlignTargetYaw = targetYaw;
                return true;
            }

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
                // the same "add the live slider offset" idiom the now-removed processor align used for the
                // froggy algae slider, so the piece itself - not just the robot's nominal center - ends up
                // over the true scoring line.
                var coralSliderOffsetInches = _gamePieceStatus.FroggyCoralSliderOffsetMeters / INCHES_TO_METERS;
                xOffsetInches += _l1Slide + coralSliderOffsetInches;
            }

            var localOffset = new Vector3(xOffsetInches, offset.yOffset, zOffset) * INCHES_TO_METERS;
            targetPosition = node.position + targetRotation * localOffset;

            targetRotation *= Quaternion.Euler(0, offset.Rotation, 0);
            targetYaw = targetRotation.eulerAngles.y;

            _reefAlignLeft = wantsLeftSide;
            _reefAlignTargetPosition = targetPosition;
            _reefAlignTargetYaw = targetYaw;
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

            SyncPidConstants(_xPidController, drivePID);
            SyncPidConstants(_zPidController, drivePID);
            SyncPidConstants(_rotatePidController, rotatePID);

            var outputX = _xPidController.UpdateLinear(dt, transform.position.x, targetPosition.x);
            var outputZ = _zPidController.UpdateLinear(dt, transform.position.z, targetPosition.z);

            // rotate's gain is tuned for degrees (carried over from the old degree-based PIDController.UpdateAngle),
            // so feed ToMathYaw's radians through Rad2Deg first - UpdateAngle wraps the difference itself via
            // AngleDifference, same as ReefscapeAutoAlign's own rotatePidController.UpdateAngle call.
            var currentYawDegrees = ToMathYaw(transform.eulerAngles.y) * Mathf.Rad2Deg;
            var targetYawDegrees2 = ToMathYaw(targetYawDegrees) * Mathf.Rad2Deg;
            var rotateOutput = _rotatePidController.UpdateAngle(dt, currentYawDegrees, targetYawDegrees2);

            var translateOutput = new Vector2(outputX, outputZ);
            if (translateOutput.magnitude > drivePID.Max)
            {
                translateOutput = translateOutput.normalized * drivePID.Max;
            }

            _driveController.overideInput(translateOutput, rotateOutput, DriveController.DriveMode.FieldOriented);
        }

        private void ResetPid()
        {
            _xPidController.ResetController();
            _zPidController.ResetController();
            _rotatePidController.ResetController();
        }

        // Same as ReefscapeAutoAlign's own UpdatePid: lets kP/kI/kD/Max be re-tuned live in the Inspector at
        // runtime (Play mode), resetting the controller's derivative/integral state whenever gains change so
        // a mid-tune edit doesn't spike the output off stale state.
        private static void SyncPidConstants(PIDController pidController, PidConstants pidConstants)
        {
            if (!Mathf.Approximately(pidConstants.kP, pidController.proportionalGain) ||
                !Mathf.Approximately(pidConstants.kI, pidController.integralGain) ||
                !Mathf.Approximately(pidConstants.kD, pidController.derivativeGain) ||
                !Mathf.Approximately(pidConstants.Isaturation, pidController.integralSaturation))
            {
                pidController.proportionalGain = pidConstants.kP;
                pidController.integralGain = pidConstants.kI;
                pidController.derivativeGain = pidConstants.kD;
                pidController.integralSaturation = pidConstants.Isaturation;
                pidController.ResetController();
            }

            if (!Mathf.Approximately(pidConstants.Max, pidController.outputMax))
            {
                pidController.outputMax = pidConstants.Max;
                pidController.outputMin = -pidConstants.Max;
            }
        }

        // Matches the yaw convention used by 340's proven GRRAutoAlign: converts Unity's left-handed Y euler
        // (0 = +Z, clockwise-positive) into a standard math angle (0 = +X, counter-clockwise-positive) so the
        // wrapped angle error can be computed the same well-tested way.
        private static float ToMathYaw(float unityYawDegrees)
        {
            return -Mathf.Deg2Rad * (unityYawDegrees - 90f);
        }

        /// <summary>True while this component is actively driving the robot toward the barge.</summary>
        public bool BargeAlignActive() => _bargeAlignActive;

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

        /// <summary>
        /// False only in the window between L4 align engaging (see TryAlignToReefNode's isL4NotReady branch)
        /// and the robot getting at least l4StandoffInches away from the align node it's scoring - read by
        /// StuyPulseClean/StuyPulseNewArmClean's HandleL4 to hold the superstructure below L4 during that
        /// approach instead of raising while still closer than the standoff, since L4's elevator/arm sweep
        /// through space the robot would otherwise already be sitting in. Sticky per engagement (once far
        /// enough, stays ready even if the robot then drives in closer while the setpoint finishes raising).
        /// Deliberately measures distance to the align node (the reef branch face), not the separate reef
        /// algae node/spot AlgaeReadyForSetpoint tracks - those are unrelated targets that can sit far apart
        /// on the same face. True (i.e. "go ahead, raise to L4") whenever L4 align isn't currently engaged at
        /// all, so a manually-picked L4 setpoint (no align button held) is never blocked.
        /// </summary>
        public bool L4ReadyForSetpoint() => !_l4Engaged || _l4ReachedStandoff;

        /// <summary>True while this component is actively driving the robot toward a reef branch.</summary>
        public bool ReefAlignActive() => _reefAlignActive;

        /// <summary>True if the reef branch currently being targeted is the left one.</summary>
        public bool ReefAlignLeft() => _reefAlignLeft;

        /// <summary>True once reef/coral align has actually arrived at its computed target (position + yaw
        /// within tolerance), not just while it's still driving in. Used by the LED controller to distinguish
        /// "en route" (blink) from "there" (solid).</summary>
        public bool ReefAlignAtTarget()
        {
            if (!_reefAlignActive) return false;

            var positionError = Vector3.Distance(
                new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(_reefAlignTargetPosition.x, 0f, _reefAlignTargetPosition.z));
            if (positionError > REEF_ALIGN_POSITION_TOLERANCE_METERS) return false;

            var currentYaw = ToMathYaw(transform.eulerAngles.y);
            var targetYaw = ToMathYaw(_reefAlignTargetYaw);
            var angleErrorDegrees = Mathf.Abs(Mathf.Repeat(targetYaw - currentYaw + Mathf.PI, 2f * Mathf.PI) - Mathf.PI) * Mathf.Rad2Deg;
            return angleErrorDegrees <= REEF_ALIGN_YAW_TOLERANCE_DEGREES;
        }
    }
}
