using System.Collections;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// Small surface the sibling StuyPulseAutoAlign component reads instead of holding its own references
    /// to (and comparing against) the froggy coral stow / shooter algae stow GamePieceStates, or the
    /// protected LastSetpoint, directly.
    /// </summary>
    public interface IStuyPulseGamePieceStatus
    {
        /// <summary>True while a coral is docked in the froggy/L1 holder.</summary>
        bool HasFroggyCoral { get; }

        /// <summary>True while an algae is docked in the shooter, ready to score (barge/processor).</summary>
        bool HasShooterAlgae { get; }

        /// <summary>
        /// True while an algae is docked in froggy instead of the shooter - TryReleaseShooterAlgae's else
        /// branch scores this straight out of froggy (FroggyState.AlgaeOuttake) rather than through the
        /// shooter, which needs a different facing at the processor.
        /// </summary>
        bool HasFroggyAlgae { get; }

        /// <summary>
        /// True right after scoring/backing out of L4 if the driver has switched to Algae mode - the arm has
        /// farther to swing to reposition for algae than for another coral level, so reef align should hold
        /// a bigger standoff distance for this transition instead of pulling the robot in close as normal.
        /// </summary>
        bool WantsExtraReefClearance { get; }

        /// <summary>
        /// True while the driver is going for algae - either fully in Algae mode, sitting at one of the
        /// algae grab setpoints, or mid-way through the two-step "grabbed algae off the reef, now seating it
        /// in the shooter" handoff. Station align should never engage while this is true.
        /// </summary>
        bool IsIntakingAlgae { get; }

        /// <summary>
        /// How far the algae currently held in froggy sits off-center on its slider, in meters, signed the
        /// same way UpdateFroggySliderVisuals reads it (local X of froggyAlgaeTarger). Zero when froggy isn't
        /// holding algae. Processor align adds this to its target so the piece - not just the robot's nominal
        /// center - ends up lined up with the processor opening.
        /// </summary>
        float FroggyAlgaeSliderOffsetMeters { get; }

        /// <summary>
        /// How far the coral currently held in froggy sits off-center on its slider, in meters, signed the
        /// same way UpdateFroggySliderVisuals reads it (local Z of forggyCoralTarget). Zero when froggy isn't
        /// holding coral. L1/froggy reef align adds this to its target for the same reason as the algae
        /// version above.
        /// </summary>
        float FroggyCoralSliderOffsetMeters { get; }

        /// <summary>
        /// True while the superstructure (elevator + arm) has actually reached the front/back Low or High
        /// algae setpoint matching the current CurrentSetpoint/facing combo - false the rest of the time,
        /// including while CurrentSetpoint is Low/HighAlgae but the arm is still mid-transition. Lets algae
        /// align hold a farther-back standoff until the mechanism is actually in position, then pull in to
        /// the normal close standoff once it's ready.
        /// </summary>
        bool IsAtAlgaeSetpoint { get; }

        /// <summary>
        /// True while the superstructure (elevator + arm) has actually reached the front/back L4 setpoint
        /// matching the current facing - false the rest of the time, including while CurrentSetpoint is L4
        /// but the arm is still mid-transition. Lets reef branch align hold the algae standoff point until
        /// the mechanism is actually in position for L4, then pull in to the normal (much closer) L4 scoring
        /// offset once it's ready - L4's elevator extension and arm swing sweep through space the robot would
        /// otherwise already be sitting in.
        /// </summary>
        bool IsAtL4Setpoint { get; }
    }

    /// <summary>
    /// Cleaned-up rewrite of StuyPulse.cs (the regular/Champs-era arm). Behavior is unchanged from the
    /// original - same setpoints, same offsets, same scoring physics, same serialized fields so it can be
    /// wired up the same way on a duplicated prefab variant. The code is reorganized to mirror the real
    /// robot's subsystem split (github.com/StuyPulse/Aunt-Mary/releases/tag/Champs), which is what this
    /// arm actually maps to:
    ///
    ///   elevator + eeArm            -> SuperStructure (elevator + arm)
    ///   shooter wheel joints        -> Shooter (roller speed by state)
    ///   froggy pivot + rollers      -> Froggy (pivot angle + roller speed by state)
    ///   funnel rollers              -> Funnel
    ///   climbPivot1 / climbPivot2   -> Climb
    ///
    /// Each of those becomes its own Update*() method instead of one long FixedUpdate, and the big nested
    /// PlacePiece if-chain becomes one Try*() helper per real scoring scenario.
    ///
    /// Reef branch, human player station, and barge alignment are all handled by the sibling
    /// StuyPulseAutoAlign component now, not by the framework's ReefscapeAutoAlign - see that file for details.
    /// </summary>
    public class StuyPulseClean : ReefscapeRobotBase, IStuyPulseGamePieceStatus
    {
        [Header("SuperStructure (Elevator + Arm)")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint eeArm;

        [Header("Froggy Pivot")]
        [SerializeField] private GenericJoint froggy;

        [Header("Climb")]
        [SerializeField] private GenericJoint climbPivot1;
        [SerializeField] private GenericJoint climbPivot2;

        [Header("PID Constants")]
        [SerializeField] private PidConstants eeArmPid;
        [SerializeField] private PidConstants froggyPid;
        [SerializeField] private PidConstants climbPivotsPid;

        [Header("Setpoints")]
        [SerializeField] private StuyPulseSetpoint stow;
        [SerializeField] private StuyPulseSetpoint intakeFunnel;
        [SerializeField] private StuyPulseSetpoint eeL1;
        [SerializeField] private StuyPulseSetpoint frontL2;
        [SerializeField] private StuyPulseSetpoint backL2;
        [SerializeField] private StuyPulseSetpoint frontL3;
        [SerializeField] private StuyPulseSetpoint backL3;
        [SerializeField] private StuyPulseSetpoint frontL4;
        [SerializeField] private StuyPulseSetpoint backL4;
        [SerializeField] private StuyPulseSetpoint backL4Scored;

        [SerializeField] private StuyPulseSetpoint lollipopIntake;
        [SerializeField] private StuyPulseSetpoint frontLowAlgae;
        [SerializeField] private StuyPulseSetpoint frontHighAlgae;
        [SerializeField] private StuyPulseSetpoint backLowAlgae;
        [SerializeField] private StuyPulseSetpoint backHighAlgae;
        [SerializeField] private StuyPulseSetpoint bargePrep;
        [SerializeField] private StuyPulseSetpoint bargePlace;
        [SerializeField] private StuyPulseSetpoint process;

        [SerializeField] private StuyPulseSetpoint froggyCoral;
        [SerializeField] private StuyPulseSetpoint froggyAlgae;
        [SerializeField] private StuyPulseSetpoint froggyLollipop;
        [SerializeField] private StuyPulseSetpoint froggyCoralPlace;
        [SerializeField] private StuyPulseSetpoint froggyAlgaeProcess;

        [SerializeField] private StuyPulseSetpoint climbStow;
        [SerializeField] private StuyPulseSetpoint climbPrep;
        [SerializeField] private StuyPulseSetpoint climbClimb;

        [Header("Intakes and Stow Slots")]
        [SerializeField] private ReefscapeGamePieceIntake funnelCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake shooterAlgaeIntake;
        [SerializeField] private ReefscapeGamePieceIntake froggyCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake froggyAlgaeIntake;

        [SerializeField] private GamePieceState shooterCoralStowState;
        [SerializeField] private GamePieceState shooterAlgaeStowState;

        [SerializeField] private GamePieceState froggyCoralStowState;
        [SerializeField] private GamePieceState froggyAlgaeStowState;

        [Header("Froggy Slider Visuals")]
        [SerializeField] private Transform forggyCoralTarget;
        [SerializeField] private Transform frogyCoralSlid;
        [SerializeField] private Transform froggyAlgaeTarger;
        [SerializeField] private Transform froggyAlgaeSlider;

        [Header("Froggy & Funnel Rollers")]
        [SerializeField] private GenericRoller[] froggyRollers;
        [SerializeField] private GenericRoller[] funnelRollers;

        [Header("Scoring Colliders")]
        [SerializeField] private CapsuleCollider[] froggyRollerColliders;
        [SerializeField] private BoxCollider[] collidersToDisableForFroggyCoralScoring;
        [SerializeField] private MeshCollider[] shooterCollidersForAlgae;

        [Header("Audio")]
        [SerializeField] private AudioSource funnelAudioSource;
        [SerializeField] private AudioClip funnelAudioClip;
        [SerializeField] private AudioSource endEffectorAudioSource;
        [SerializeField] private AudioClip endEffectorAudioClip;
        [SerializeField] private AudioSource froggyAudioSource;
        [SerializeField] private AudioClip froggyAudioClip;

        [Header("Shooter Animation Wheels")]
        [SerializeField] private GenericAnimationJoint[] shooterWheelsTop;
        [SerializeField] private GenericAnimationJoint[] shooterBottomWheels;
        [SerializeField] private float shooterAnimationWheelSpeeds = 300;

        [Header("Froggy Animation Wheels")]
        [SerializeField] private GenericAnimationJoint[] froggyGreenRollerWheels;
        [SerializeField] private GenericAnimationJoint[] froggyOrangeRollerWheels;
        [SerializeField] private float froggyAnimationWheelSpeeds = 150;

        [SerializeField] private FroggyState frogState = FroggyState.Stow;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;
        private StuyPulseAutoAlign _autoAlign;
        private float _defaultCoralStationDropDistance;

        private float _elevatorTargetHeight;
        private float _eeArmTargetAngle;
        private float _froggyTargetAngle;
        private float _climbPivot1TargetAngle;
        private float _climbPivot2TargetAngle;

        private bool stillInPlaceState;
        private bool froggyLolli;

        private float _funnelWheels;
        private float _froggyWheels;
        private bool _isIntaking;
        private float _outtakeAudioUntil;
        private float _froggyOuttakeAudioUntil;

        // Timestamps of when froggy coral / shooter algae were each most recently newly acquired (null while
        // not held) - lets ResolveStackOrder tell which of the two, if both are currently held, was grabbed
        // first, per the driver's stack-button request.
        private float? _froggyCoralAcquiredAt;
        private float? _shooterAlgaeAcquiredAt;

        private float froggyWheelSpeeds;
        private float shooterWheelSpeeds;

        private Vector3 _blueReef;
        private Vector3 _redReef;

        // ReefscapeRobotBase.LastSetpoint is meant to hold the setpoint from before the current transition,
        // but its update logic reassigns it to the new CurrentSetpoint in the same Update() call that changes
        // CurrentSetpoint, so external readers never actually see the old value - it always just mirrors
        // CurrentSetpoint. That's shared framework code other mods depend on, so instead of touching it this
        // tracks the same "previous distinct setpoint" concept locally, correctly delayed by one FixedUpdate.
        private ReefscapeSetpoints _trackedSetpoint;
        private ReefscapeSetpoints _priorDistinctSetpoint;

        public bool HasFroggyCoral => _coralController.currentStateNum == froggyCoralStowState.stateNum && _coralController.atTarget;
        public bool HasShooterAlgae => _algaeController.currentStateNum == shooterAlgaeStowState.stateNum && _algaeController.atTarget;
        public bool HasFroggyAlgae => _algaeController.currentStateNum == froggyAlgaeStowState.stateNum && _algaeController.atTarget;
        public bool WantsExtraReefClearance => _priorDistinctSetpoint == ReefscapeSetpoints.L4 && CurrentRobotMode == ReefscapeRobotMode.Algae;

        public bool IsIntakingAlgae =>
            CurrentRobotMode == ReefscapeRobotMode.Algae ||
            CurrentSetpoint is ReefscapeSetpoints.Stack or ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae ||
            (CurrentSetpoint == ReefscapeSetpoints.Intake && LastSetpoint is ReefscapeSetpoints.Stack or ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae);

        // Matches the exact front/back setpoint HandleLowAlgae/HandleHighAlgae drive toward for the current
        // facing, so this stays in sync with whichever one the arm is actually being commanded to right now.
        public bool IsAtAlgaeSetpoint =>
            CurrentSetpoint == ReefscapeSetpoints.LowAlgae && SuperstructureAtSetpoint(IsFacingReef(GetClosestReef()) ? frontLowAlgae : backLowAlgae) ||
            CurrentSetpoint == ReefscapeSetpoints.HighAlgae && SuperstructureAtSetpoint(IsFacingReef(GetClosestReef()) ? frontHighAlgae : backHighAlgae);

        public bool IsAtL4Setpoint =>
            CurrentSetpoint == ReefscapeSetpoints.L4 && SuperstructureAtSetpoint(IsFacingReef(GetClosestReef()) ? frontL4 : backL4);

        // Reads the frozen slider-visual transforms rather than the intakes' own GamePiece, since
        // RequestIntake(..., false) - called by every state handler except the moment a piece is actively
        // being secured - nulls the intake's GamePiece out (see GamePieceIntake<T,D>.RequestIntake). By the
        // time the robot is sitting at Processor/L1 with the piece already docked, GamePiece is always null,
        // but UpdateFroggySliderVisuals() only writes these transforms while GamePiece is non-null, so they
        // hold the last real reading - exactly the slide position the piece was secured at.
        public float FroggyAlgaeSliderOffsetMeters => froggyAlgaeSlider.localPosition.x;
        public float FroggyCoralSliderOffsetMeters => frogyCoralSlid.localPosition.z;

        protected override void Start()
        {
            base.Start();
            SetRobotMode(ReefscapeRobotMode.Coral);

            eeArm.SetPid(eeArmPid);
            froggy.SetPid(froggyPid);
            climbPivot1.SetPid(climbPivotsPid);
            climbPivot2.SetPid(climbPivotsPid);

            _autoAlign = GetComponent<StuyPulseAutoAlign>();
            _defaultCoralStationDropDistance = CurrentCoralStationMode.DropDistance;

            RobotGamePieceController.SetPreload(shooterCoralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[] { shooterCoralStowState, froggyCoralStowState };
            _coralController.intakes.Add(funnelCoralIntake);
            _coralController.intakes.Add(froggyCoralIntake);

            _algaeController.gamePieceStates = new[] { shooterAlgaeStowState, froggyAlgaeStowState };
            _algaeController.intakes.Add(shooterAlgaeIntake);
            _algaeController.intakes.Add(froggyAlgaeIntake);

            _blueReef = GameObject.Find("BlueReef").transform.position;
            _redReef = GameObject.Find("RedReef").transform.position;

            SetupLoopingAudio(funnelAudioSource, funnelAudioClip);
            SetupLoopingAudio(endEffectorAudioSource, endEffectorAudioClip);
            SetupLoopingAudio(froggyAudioSource, froggyAudioClip);
        }

        private static void SetupLoopingAudio(AudioSource source, AudioClip clip)
        {
            if (source == null || clip == null) return;
            source.clip = clip;
            source.volume = 0.2f;
            source.loop = true;
            source.Stop();
        }

        private void FixedUpdate()
        {
            if (CurrentSetpoint != _trackedSetpoint)
            {
                _priorDistinctSetpoint = _trackedSetpoint;
                _trackedSetpoint = CurrentSetpoint;
            }

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                funnelAudioSource?.Stop();
                endEffectorAudioSource?.Stop();
                froggyAudioSource?.Stop();
                return;
            }

            var hasCoral = _coralController.HasPiece();
            var hasAlgae = _algaeController.HasPiece();
            var shooterHasCoral = _coralController.currentStateNum == shooterCoralStowState.stateNum && _coralController.atTarget;
            var shooterHasAlgae = _algaeController.currentStateNum == shooterAlgaeStowState.stateNum && _algaeController.atTarget;

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                foreach (var roller in funnelRollers) roller.flipVelocity();
            }

            if (!OuttakeAction.IsPressed() && !IntakeAction.IsPressed())
            {
                SetRollerSpeeds(0, 0);
            }

            _isIntaking = false;

            _funnelWheels = 0f;
            if (IntakeAction.IsPressed() && !hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                _funnelWheels = 900f;
                _isIntaking = true;
            }

            _froggyWheels = 0f;
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1 && IntakeAction.IsPressed())
            {
                if (!hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral) _froggyWheels = 2000f;
                else if (!hasAlgae && CurrentRobotMode == ReefscapeRobotMode.Algae) _froggyWheels = 6000f;
            }

            // Only one game piece can sit in the shooter stow slot at a time - a piece already docked there
            // blocks the other controller's shooter-side intake, same as the real Shooter only holding one game piece.
            if (shooterHasCoral) _algaeController.RequestIntake(shooterAlgaeIntake, false);
            else if (shooterHasAlgae) _coralController.RequestIntake(funnelCoralIntake, false);

            UpdateFroggySliderVisuals();

            if (CurrentSetpoint != ReefscapeSetpoints.Place || RobotModeToggleAction.IsPressed())
            {
                stillInPlaceState = false;
            }

            CurrentCoralStationMode.DropType = CurrentIntakeMode == ReefscapeIntakeMode.L1 ? DropType.Ground : DropType.Station;
            CurrentCoralStationMode.DropDistance = hasCoral ? 0f : _defaultCoralStationDropDistance;

            if (LastSetpoint == ReefscapeSetpoints.Intake && CurrentIntakeMode == ReefscapeIntakeMode.L1 && !hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                _coralController.SetTargetState(froggyCoralStowState);
                _coralController.RequestIntake(froggyCoralIntake, true);
                _coralController.RequestIntake(funnelCoralIntake, false);
                _isIntaking = true;
            }

            if (LastSetpoint == ReefscapeSetpoints.Place) frogState = FroggyState.Stow;

            if (LastSetpoint == ReefscapeSetpoints.Place && CurrentSetpoint == ReefscapeSetpoints.Stow)
            {
                foreach (var col in collidersToDisableForFroggyCoralScoring) col.enabled = true;
            }

            ResolveStackOrder();

            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: HandleStow(hasCoral, shooterHasCoral, shooterHasAlgae); break;
                case ReefscapeSetpoints.Intake: HandleIntake(hasCoral, hasAlgae, shooterHasAlgae); break;
                case ReefscapeSetpoints.Place: HandlePlace(shooterHasCoral, shooterHasAlgae); break;
                case ReefscapeSetpoints.L1: HandleL1(shooterHasCoral); break;
                case ReefscapeSetpoints.Stack: HandleStack(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L2: HandleL2(shooterHasCoral); break;
                case ReefscapeSetpoints.LowAlgae: HandleLowAlgae(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L3: HandleL3(shooterHasCoral); break;
                case ReefscapeSetpoints.HighAlgae: HandleHighAlgae(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L4: HandleL4(shooterHasCoral); break;
                case ReefscapeSetpoints.Processor: HandleProcessor(shooterHasAlgae); break;
                case ReefscapeSetpoints.Barge: HandleBarge(shooterHasAlgae); break;
                case ReefscapeSetpoints.RobotSpecial:
                {
                    froggyLolli = !froggyLolli;
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                }
                case ReefscapeSetpoints.Climb: HandleClimb(); break;
                case ReefscapeSetpoints.Climbed: HandleClimbed(); break;
            }

            ApplySuperStructureTargets();
            UpdateAudio();
            ApplyRollerOutputs();
            UpdateFroggyRollers();
        }

        private void UpdateFroggySliderVisuals()
        {
            if (froggyCoralIntake.GamePiece != null)
            {
                var localZ = forggyCoralTarget.transform.InverseTransformPoint(froggyCoralIntake.GamePiece.transform.position).z;
                frogyCoralSlid.localPosition = new Vector3(0, 0, localZ);
            }

            if (froggyAlgaeIntake.GamePiece != null)
            {
                var localX = froggyAlgaeTarger.transform.InverseTransformPoint(froggyAlgaeIntake.GamePiece.transform.position).x;
                froggyAlgaeSlider.localPosition = new Vector3(localX, 0, 0);
            }
        }

        // ---- SuperStructure (elevator + eeArm + climb) setpoint handlers, one per driver-requested state ----

        private void HandleStow(bool hasCoral, bool shooterHasCoral, bool shooterHasAlgae)
        {
            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(stow);
            frogState = FroggyState.Stow;

            var stowIntaking = CurrentIntakeMode != ReefscapeIntakeMode.L1 && IntakeAction.IsPressed() && !shooterHasCoral && !shooterHasAlgae;
            _coralController.RequestIntake(funnelCoralIntake, stowIntaking && SuperstructureAtSetpoint(stow));
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            if (stowIntaking && !hasCoral) _isIntaking = true;

            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
            foreach (var col in froggyRollerColliders) col.enabled = true;

            // Tied to stowIntaking (same gate as the audio above) rather than just "shooter is empty" -
            // previously this spun the shooter wheels any time the shooter was empty, even after letting go
            // of intake without securing a coral, leaving the wheels visibly spinning with no intake sound.
            SetRollerSpeeds(0, stowIntaking ? shooterAnimationWheelSpeeds : 0);
        }

        private void HandleIntake(bool hasCoral, bool hasAlgae, bool shooterHasAlgae)
        {
            // These first two branches use _coralController.atTarget (not the hasCoral param, i.e. HasPiece())
            // to decide whether to keep the coral-side rollers spinning - HasPiece() flips true as soon as
            // the piece is secured/attached to the intake, well before it's actually finished animating into
            // its stow slot (atTarget only flips true once that motion completes, see
            // RobotGamePieceController.ProcessNode). Gating on hasCoral cut the rollers the instant the piece
            // attached, before it was actually pulled all the way in.
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1 && !_coralController.atTarget && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                SetSetpoint(froggyCoral);
                frogState = FroggyState.CoralIntake;
                _froggyWheels = 2000f;
                _coralController.SetTargetState(froggyCoralStowState);
                _coralController.RequestIntake(froggyCoralIntake);
                _coralController.RequestIntake(funnelCoralIntake, false);
                _isIntaking = true;
                SetRollerSpeeds(froggyAnimationWheelSpeeds, 0);
            }
            else if (CurrentRobotMode == ReefscapeRobotMode.Coral && !_coralController.atTarget && !shooterHasAlgae && CurrentIntakeMode != ReefscapeIntakeMode.L1)
            {
                if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(intakeFunnel);
                frogState = FroggyState.Stow;
                _coralController.SetTargetState(shooterCoralStowState);
                _coralController.RequestIntake(funnelCoralIntake, SuperstructureAtSetpoint(intakeFunnel));
                _coralController.RequestIntake(froggyCoralIntake, false);
                _isIntaking = true;
                SetRollerSpeeds(0, shooterAnimationWheelSpeeds);
            }
            else if (!hasCoral && (!hasAlgae || hasAlgae && !shooterHasAlgae) &&
                     (LastSetpoint == ReefscapeSetpoints.HighAlgae || LastSetpoint == ReefscapeSetpoints.LowAlgae || LastSetpoint == ReefscapeSetpoints.Stack))
            {
                frogState = FroggyState.Stow;
                _algaeController.SetTargetState(shooterAlgaeStowState);
                _algaeController.RequestIntake(shooterAlgaeIntake);
                _algaeController.RequestIntake(froggyAlgaeIntake, false);
                _isIntaking = true;
                SetRollerSpeeds(0, -shooterAnimationWheelSpeeds);
            }
            else if (CurrentRobotMode == ReefscapeRobotMode.Algae && !hasAlgae)
            {
                frogState = FroggyState.AlgaeIntake;
                UpdateFroggyRollers();
                SetSetpoint(froggyLolli ? froggyLollipop : froggyAlgae);
                _algaeController.SetTargetState(froggyAlgaeStowState);
                _algaeController.RequestIntake(froggyAlgaeIntake);
                _algaeController.RequestIntake(shooterAlgaeIntake, false);
                _froggyWheels = 6000f;
                _isIntaking = true;
                SetRollerSpeeds(-froggyAnimationWheelSpeeds, 0);
            }
            else
            {
                // None of the intake branches above matched - most commonly a piece just secured mid-intake
                // (hasCoral/hasAlgae flipped true) while CurrentSetpoint is still Intake. shooterWheelSpeeds/
                // froggyWheelSpeeds are persistent fields (see SetRollerSpeeds), not one-shot pulses, so
                // without this the rollers stay latched at whatever intake speed was set the tick before
                // securing and keep spinning indefinitely - same bug as HandleLowAlgae/HandleHighAlgae.
                SetRollerSpeeds(0, 0);
            }
        }

        private void HandlePlace(bool shooterHasCoral, bool shooterHasAlgae)
        {
            if (stillInPlaceState) return;

            if (shooterHasAlgae && LastSetpoint == ReefscapeSetpoints.Barge) SetSetpoint(bargePlace);
            else if (shooterHasCoral && LastSetpoint == ReefscapeSetpoints.L4) SetFacingSetpoint(frontL4, backL4Scored);

            HandlePlaceScoring();
        }

        private void HandleL1(bool shooterHasCoral)
        {
            if (!shooterHasCoral)
            {
                // Same reef-clearance guard as the scoring branch below - without it, going straight from a
                // just-scored L4 into L1 mode let froggyCoralPlace's intake pose swing in immediately while
                // still physically close to the reef, clipping it. Wait until the arm's left L4 (or the robot
                // has backed off far enough) before moving to the froggy intake pose.
                if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(froggyCoralPlace);
            }
            else if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(eeL1);
            frogState = FroggyState.Stow;

            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        // ReefscapeRobotBase.Update() resolves the stack button (L1Action) to Processor whenever any algae is
        // held, regardless of order - but the driver wants it order-dependent: coral-in-froggy grabbed first
        // then algae should go Processor, algae grabbed first then coral should go L1. CurrentSetpoint's
        // setter is private to the base class, so this can't be fixed there; instead it's corrected here,
        // right before the switch dispatches, via the base's own protected SetState - which the framework
        // hasn't dispatched to HandleProcessor/HandleL1 yet this frame, so the correction is invisible.
        private void ResolveStackOrder()
        {
            _froggyCoralAcquiredAt = HasFroggyCoral ? _froggyCoralAcquiredAt ?? Time.time : null;
            _shooterAlgaeAcquiredAt = HasShooterAlgae ? _shooterAlgaeAcquiredAt ?? Time.time : null;

            if (!HasFroggyCoral || !HasShooterAlgae) return;
            if (CurrentSetpoint != ReefscapeSetpoints.Processor && CurrentSetpoint != ReefscapeSetpoints.L1) return;
            if (_froggyCoralAcquiredAt is not { } coralAt || _shooterAlgaeAcquiredAt is not { } algaeAt) return;

            var wantedSetpoint = coralAt <= algaeAt ? ReefscapeSetpoints.Processor : ReefscapeSetpoints.L1;
            if (CurrentSetpoint != wantedSetpoint) SetState(wantedSetpoint);
        }

        private void HandleStack(bool hasAlgae, bool shooterHasCoral)
        {
            if (shooterHasCoral || hasAlgae) return;

            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(lollipopIntake);
            _algaeController.SetTargetState(shooterAlgaeStowState);
            var stackIntaking = IntakeAction.IsPressed() && !hasAlgae;
            _algaeController.RequestIntake(shooterAlgaeIntake, stackIntaking);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            if (stackIntaking)
            {
                _isIntaking = true;
                SetRollerSpeeds(0, -shooterAnimationWheelSpeeds);
            }
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleL2(bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            if (shooterHasCoral && (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8))
            {
                SetFacingSetpoint(frontL2, backL2);
            }
            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleLowAlgae(bool hasAlgae, bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            // shooterWheelSpeeds is a persistent field (see SetRollerSpeeds), not implicitly reset - once
            // an algae is secured, everything below (including the SetRollerSpeeds(0, -shooterSpeed) intake
            // call) stops running, so without this the wheels stay latched at whatever speed they were
            // spinning the instant hasAlgae flipped true, spinning forever instead of stopping.
            if (shooterHasCoral || hasAlgae) { SetRollerSpeeds(0, 0); return; }

            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8)
            {
                if (_autoAlign == null || _autoAlign.AlgaeReadyForSetpoint()) SetFacingSetpoint(frontLowAlgae, backLowAlgae);
                else SetSetpoint(stow);
            }
            _algaeController.SetTargetState(shooterAlgaeStowState);
            var lowAlgaeIntaking = IntakeAction.IsPressed() && !hasAlgae;
            _algaeController.RequestIntake(shooterAlgaeIntake, lowAlgaeIntaking);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            if (lowAlgaeIntaking)
            {
                _isIntaking = true;
                SetRollerSpeeds(0, -shooterAnimationWheelSpeeds);
            }
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleL3(bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            if (shooterHasCoral && (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8))
            {
                SetFacingSetpoint(frontL3, backL3);
            }
            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
        }

        private void HandleHighAlgae(bool hasAlgae, bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            // See the identical comment in HandleLowAlgae - without stopping the rollers here, they stay
            // latched at whatever speed was last set the instant hasAlgae flipped true.
            if (shooterHasCoral || hasAlgae) { SetRollerSpeeds(0, 0); return; }

            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8)
            {
                if (_autoAlign == null || _autoAlign.AlgaeReadyForSetpoint()) SetFacingSetpoint(frontHighAlgae, backHighAlgae);
                else SetSetpoint(stow);
            }
            _algaeController.SetTargetState(shooterAlgaeStowState);
            var highAlgaeIntaking = IntakeAction.IsPressed() && !hasAlgae;
            _algaeController.RequestIntake(shooterAlgaeIntake, highAlgaeIntaking);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            if (highAlgaeIntaking)
            {
                _isIntaking = true;
                SetRollerSpeeds(0, -shooterAnimationWheelSpeeds);
            }
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleL4(bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            if (shooterHasCoral && (_autoAlign == null || _autoAlign.L4ReadyForSetpoint())) SetFacingSetpoint(frontL4, backL4);
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleProcessor(bool shooterHasAlgae)
        {
            SetSetpoint(shooterHasAlgae ? process : froggyAlgaeProcess);
            _algaeController.RequestIntake(shooterAlgaeIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
        }

        private void HandleBarge(bool shooterHasAlgae)
        {
            frogState = FroggyState.Stow;
            if (shooterHasAlgae) SetSetpoint(bargePrep);
            _algaeController.RequestIntake(shooterAlgaeIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
        }

        private void HandleClimb()
        {
            frogState = FroggyState.Stow;
            SetSetpoint(climbPrep);
            _algaeController.RequestIntake(shooterAlgaeIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
        }

        private void HandleClimbed()
        {
            frogState = FroggyState.Stow;
            SetSetpoint(climbClimb);
        }

        // ---- Place state: which real scoring action this Place press corresponds to ----

        private void HandlePlaceScoring()
        {
            StartOuttakeAudio();

            // Each Try* now only reports success once the underlying GamePieceControllerNode is actually
            // atTarget when it calls Release*(...) - that release silently no-ops and returns false while the
            // piece is still animating into its stow slot (see RobotGamePieceController.GamePieceControllerNode
            // .ReleaseGamePieceWithForce/WithContinuedForce, whenAtTarget defaults true). Previously every Try*
            // reported true unconditionally after calling Release*, so pressing Place in that in-between window
            // (e.g. immediately after intaking, before the piece settles) silently failed to release, and since
            // stillInPlaceState latched true regardless, HandlePlaceScoring never ran again for that Place press
            // - the piece stayed held forever, permanently blocking hasCoral-gated intake from ever firing again.
            // Only latching stillInPlaceState on an actual success means an early press just retries next
            // FixedUpdate until the piece is ready, instead of getting stuck.
            var handled = TryShootFroggyCoral() || TryReleaseShooterAlgae() || TryScoreL4Coral() || TryScoreL1Coral() || TryScoreDefaultCoral();

            stillInPlaceState = handled;
        }

        private void StartOuttakeAudio()
        {
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                _froggyOuttakeAudioUntil = Time.time + 0.35f;
                _outtakeAudioUntil = 0f;
            }
            else
            {
                _outtakeAudioUntil = Time.time + 0.35f;
                _froggyOuttakeAudioUntil = 0f;
            }
        }

        private bool TryShootFroggyCoral()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Coral && _algaeController.atTarget) ||
                LastSetpoint is ReefscapeSetpoints.L2 or ReefscapeSetpoints.L3 or ReefscapeSetpoints.L4 ||
                !_coralController.HasPiece() || !_coralController.atTarget ||
                _coralController.currentStateNum == shooterCoralStowState.stateNum && _coralController.atTarget)
            {
                return false;
            }

            StartCoroutine(ShootFroggyCoral());
            return true;
        }

        private bool TryReleaseShooterAlgae()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Algae && _coralController.atTarget) || !_algaeController.HasPiece() || !_algaeController.atTarget)
            {
                return false;
            }

            if (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && LastSetpoint == ReefscapeSetpoints.Barge)
            {
                frogState = FroggyState.Stow;
                foreach (var col in shooterCollidersForAlgae) col.enabled = false;
                _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3.3f, 7.6f));
                SetRollerSpeeds(0, shooterAnimationWheelSpeeds * 1.5f);
            }
            else if (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && LastSetpoint == ReefscapeSetpoints.Processor)
            {
                frogState = FroggyState.Stow;
                foreach (var col in shooterCollidersForAlgae) col.enabled = false;
                _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3, 0));
                SetRollerSpeeds(0, shooterAnimationWheelSpeeds / .75f);
            }
            else
            {
                foreach (var col in shooterCollidersForAlgae) col.enabled = false;
                if (_algaeController.currentStateNum == froggyAlgaeStowState.stateNum) frogState = FroggyState.AlgaeOuttake;
                _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3, 0));
                SetRollerSpeeds(froggyAnimationWheelSpeeds, 0);
                frogState = FroggyState.Stow;
            }

            return true;
        }

        private bool TryScoreL4Coral()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Coral && _algaeController.atTarget) || LastSetpoint != ReefscapeSetpoints.L4 || !_coralController.atTarget)
            {
                return false;
            }

            frogState = FroggyState.Stow;
            var facingReef = IsFacingReef(GetClosestReef());
            _coralController.ReleaseGamePieceWithForce(facingReef ? new Vector3(0, 0, -6) : new Vector3(0, 0, 5));
            SetRollerSpeeds(0, facingReef ? -shooterAnimationWheelSpeeds : shooterAnimationWheelSpeeds);
            return true;
        }

        private bool TryScoreL1Coral()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Coral && !_algaeController.atTarget) ||
                LastSetpoint != ReefscapeSetpoints.L1 || CurrentIntakeMode != ReefscapeIntakeMode.Normal || !_coralController.atTarget)
            {
                return false;
            }

            frogState = FroggyState.Stow;
            _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3.5f), 0.2f, .9f);
            SetRollerSpeeds(0, shooterAnimationWheelSpeeds);
            return true;
        }

        private bool TryScoreDefaultCoral()
        {
            if (CurrentRobotMode != ReefscapeRobotMode.Coral && _algaeController.atTarget) return false;
            if (!_coralController.atTarget) return false;

            frogState = FroggyState.Stow;
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 5));
            SetRollerSpeeds(0, shooterAnimationWheelSpeeds);
            return true;
        }

        private IEnumerator ShootFroggyCoral()
        {
            SetRollerSpeeds(-froggyAnimationWheelSpeeds, 0);
            frogState = FroggyState.CoralOuttake;
            foreach (var col in collidersToDisableForFroggyCoralScoring) col.enabled = false;
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 1.5f, 0));

            yield return new WaitForSeconds(1f);

            foreach (var col in collidersToDisableForFroggyCoralScoring) col.enabled = true;
            frogState = FroggyState.Stow;
            SetRollerSpeeds(0, 0);
        }

        // ---- Shared helpers ----

        private void SetSetpoint(StuyPulseSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _eeArmTargetAngle = setpoint.eeArmAngle;
            _froggyTargetAngle = setpoint.froggyAngle;
            _climbPivot1TargetAngle = setpoint.climbPivot1Angle;
            _climbPivot2TargetAngle = setpoint.climbPivot2Angle;
        }

        private void SetRollerSpeeds(float froggySpeed, float shooterSpeed)
        {
            froggyWheelSpeeds = -froggySpeed;
            shooterWheelSpeeds = shooterSpeed;
        }

        private void ApplySuperStructureTargets()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            eeArm.SetTargetAngle(_eeArmTargetAngle).withAxis(JointAxis.X).noWrap(20);
            froggy.SetTargetAngle(_froggyTargetAngle).withAxis(JointAxis.X).noWrap(-110);
            climbPivot1.SetTargetAngle(_climbPivot1TargetAngle).withAxis(JointAxis.X).noWrap(140);
            climbPivot2.SetTargetAngle(-1 * _climbPivot2TargetAngle).withAxis(JointAxis.X).noWrap(-140);
        }

        private void ApplyRollerOutputs()
        {
            foreach (var joint in froggyOrangeRollerWheels) joint.VelocityRoller(froggyWheelSpeeds);
            foreach (var joint in froggyGreenRollerWheels) joint.VelocityRoller(-froggyWheelSpeeds);
            foreach (var joint in shooterWheelsTop) joint.VelocityRoller(shooterWheelSpeeds);
            foreach (var joint in shooterBottomWheels) joint.VelocityRoller(-shooterWheelSpeeds);
        }

        private void UpdateAudio()
        {
            // Same fix as HandleIntake's coral branches: gate on _coralController.atTarget, not
            // HasPiece(), so the intake audio keeps playing through the full seating animation instead
            // of cutting out the instant the coral attaches to the intake.
            var coralAtTarget = _coralController.atTarget;
            var hasAlgae = _algaeController.HasPiece();
            var isFroggyMode = CurrentIntakeMode == ReefscapeIntakeMode.L1;
            var isStationMode = !isFroggyMode;

            var froggyIntaking = isFroggyMode && Mathf.Abs(_froggyWheels) > 1e-6;
            var froggyOuttaking = Time.time < _froggyOuttakeAudioUntil;
            PlayOrStop(froggyAudioSource, (froggyIntaking || froggyOuttaking) && isFroggyMode);

            var funnelIntaking = isStationMode && IntakeAction.IsPressed() && !coralAtTarget && !hasAlgae;
            var funnelOuttaking = isStationMode && Time.time < _outtakeAudioUntil;
            PlayOrStop(funnelAudioSource, (funnelIntaking || funnelOuttaking) && isStationMode);

            PlayOrStop(endEffectorAudioSource, _isIntaking && !coralAtTarget && !hasAlgae);
        }

        private static void PlayOrStop(AudioSource source, bool shouldPlay)
        {
            if (shouldPlay)
            {
                if (source?.isPlaying != true) source?.Play();
            }
            else
            {
                source?.Stop();
            }
        }

        private void LateUpdate()
        {
            eeArm.UpdatePid(eeArmPid);
            froggy.UpdatePid(froggyPid);
            climbPivot1.UpdatePid(climbPivotsPid);
            climbPivot2.UpdatePid(climbPivotsPid);
        }

        // ---- Froggy roller state -> speed (mirrors real Froggy.RollerState) ----

        private void UpdateFroggyRollers()
        {
            switch (frogState)
            {
                case FroggyState.Stow:
                    froggyRollers[0].stopAngularVelocity();
                    froggyRollers[1].stopAngularVelocity();
                    break;
                case FroggyState.CoralIntake:
                    froggyRollers[0].SetAngularVelocity(1000);
                    froggyRollers[1].SetAngularVelocity(-6000);
                    break;
                case FroggyState.CoralOuttake:
                    froggyRollers[0].SetAngularVelocity(-2000);
                    froggyRollers[1].SetAngularVelocity(2000);
                    break;
                case FroggyState.AlgaeIntake:
                    froggyRollers[0].SetAngularVelocity(-5000);
                    froggyRollers[1].SetAngularVelocity(0);
                    break;
                case FroggyState.AlgaeOuttake:
                    froggyRollers[0].SetAngularVelocity(0);
                    froggyRollers[1].SetAngularVelocity(-1000);
                    break;
            }
        }

        // ---- Field geometry helpers ----

        private float DistanceToReef(Vector3 reefPos)
        {
            return Mathf.Sqrt(Mathf.Pow(transform.position.x - reefPos.x, 2) + Mathf.Pow(transform.position.z - reefPos.z, 2));
        }

        // Near the field midline the two reefs are nearly equidistant, so a raw "which is closer" pick
        // flips on the same kind of per-frame noise the facing dot product does below - and unlike heading
        // noise, flipping WHICH reef is closest teleports the reef position IsFacingReef() measures against
        // to the opposite side of the field, which is a far bigger discontinuity than ordinary noise. A
        // distance-margin deadband makes the pick sticky the same way: once a reef is chosen, the other one
        // has to be closer by a clear margin (not just barely) before the pick flips.
        private const float CLOSEST_REEF_DISTANCE_DEADBAND_METERS = 1f;
        private bool _cachedClosestReefIsBlue = true;

        private Vector3 GetClosestReef()
        {
            var blueDist = DistanceToReef(_blueReef);
            var redDist = DistanceToReef(_redReef);
            if (blueDist < redDist - CLOSEST_REEF_DISTANCE_DEADBAND_METERS) _cachedClosestReefIsBlue = true;
            else if (redDist < blueDist - CLOSEST_REEF_DISTANCE_DEADBAND_METERS) _cachedClosestReefIsBlue = false;
            return _cachedClosestReefIsBlue ? _blueReef : _redReef;
        }

        // Dot product hovers near 0 whenever the robot's heading is close to perpendicular to the reef
        // direction - true whenever the robot is still driving toward a far-off algae (heading hasn't
        // settled toward the reef yet) or is mid-sweep around ApplyReefAvoidance's tangent waypoint. Right
        // at 0 the raw sign flips on every tiny heading wobble, which was flapping the arm/elevator between
        // front and back setpoints every frame. A deadband around 0 makes it sticky: once facing/not-facing
        // is decided, it takes a clear swing past the deadband (not just noise) to flip again.
        private const float FACING_REEF_DOT_DEADBAND = 0.15f;
        private bool _cachedFacingReef;

        // Physically crossing the midline is a bigger discontinuity than GetClosestReef()'s deadband alone
        // can smooth over - the robot genuinely does end up on the other reef's side, so the facing pick
        // has to flip eventually, not just later. Forcing stow while inside this band means that flip lands
        // while the mechanism is already safely retracted instead of fighting between front/back setpoints
        // while straddling the midline.
        private const float MIDLINE_STOW_BAND_METERS = 1f;

        private bool IsNearMidline() => Mathf.Abs(transform.position.x) < MIDLINE_STOW_BAND_METERS;

        // Shared by every Handle* that picks a front/back setpoint off IsFacingReef(GetClosestReef()) - near
        // the midline, stows instead of computing a front/back pick at all, per the same reasoning as
        // MIDLINE_STOW_BAND_METERS above. Callers still gate whether to call this at all on their own
        // existing setpoint logic (e.g. "not already at L4"), same as before this existed.
        private void SetFacingSetpoint(StuyPulseSetpoint front, StuyPulseSetpoint back)
        {
            SetSetpoint(IsNearMidline() ? stow : IsFacingReef(GetClosestReef()) ? front : back);
        }

        private bool IsFacingReef(Vector3 reefPos)
        {
            var toReef = (reefPos - transform.position).normalized;
            var dot = Vector3.Dot(transform.forward.normalized, toReef);
            if (dot > FACING_REEF_DOT_DEADBAND) _cachedFacingReef = true;
            else if (dot < -FACING_REEF_DOT_DEADBAND) _cachedFacingReef = false;
            return _cachedFacingReef;
        }

        private bool ElevatorAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            return Utils.InRange(elevator.GetElevatorHeight(), targetSetpoint.elevatorHeight, 2f);
        }

        private bool IntakeAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            return Utils.InAngularRange(eeArm.GetSingleAxisAngle(JointAxis.X), targetSetpoint.eeArmAngle, 2f);
        }

        private bool SuperstructureAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            return IntakeAtSetpoint(targetSetpoint) && ElevatorAtSetpoint(targetSetpoint);
        }
    }
}
