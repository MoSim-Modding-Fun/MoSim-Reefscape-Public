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
    /// Cleaned-up rewrite of StuyPulseNewArm.cs (the New Arm, added after Champs - github.com/StuyPulse/Aunt-Mary
    /// main). Behavior is unchanged from the original - same setpoints, same offsets, same scoring physics,
    /// same serialized fields so it can be wired up the same way on a duplicated prefab variant. The code is
    /// reorganized to mirror the real robot's subsystem split:
    ///
    ///   elevator + eeArm            -> SuperStructure (elevator + arm)
    ///   shooter wheel joints        -> Shooter (roller speed by state)
    ///   froggy pivot + rollers      -> Froggy (pivot angle + roller speed by state)
    ///   funnel rollers              -> Funnel
    ///   climbPivot1 / climbPivot2   -> Climb
    ///
    /// Each of those becomes its own Update*() method instead of one long FixedUpdate, and the big nested
    /// PlacePiece if-chain becomes one Try*() helper per real scoring scenario. The differences from
    /// StuyPulseClean.cs (the regular arm) are exactly the differences that existed between StuyPulse.cs and
    /// StuyPulseNewArm.cs: a single "barge" setpoint instead of separate prep/place poses, no elevator
    /// clearance check before backing into the lollipop intake pose, the extra shooterCollidersForAlgae
    /// reset, and the L4 back-side coral release using a continued force instead of an instant one.
    ///
    /// Reef branch and human player station alignment are both handled by the sibling StuyPulseAutoAlign
    /// component now, not by the framework's ReefscapeAutoAlign - see that file for details.
    /// </summary>
    public class StuyPulseNewArmClean : ReefscapeRobotBase
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
        [SerializeField] private StuyPulseSetpoint stowWithAlgae;
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
        [SerializeField] private StuyPulseSetpoint barge;
        [SerializeField] private StuyPulseSetpoint process;

        [SerializeField] private StuyPulseSetpoint froggyCoral;
        [SerializeField] private StuyPulseSetpoint froggyCoralWithAlgae;
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

        private float froggyWheelSpeeds;
        private float shooterWheelSpeeds;

        private Vector3 _blueReef;
        private Vector3 _redReef;

        protected override void Start()
        {
            base.Start();
            SetRobotMode(ReefscapeRobotMode.Coral);

            eeArm.SetPid(eeArmPid);
            froggy.SetPid(froggyPid);
            climbPivot1.SetPid(climbPivotsPid);
            climbPivot2.SetPid(climbPivotsPid);

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
                foreach (var col in shooterCollidersForAlgae) col.enabled = true;
            }

            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: HandleStow(hasCoral, shooterHasCoral, shooterHasAlgae); break;
                case ReefscapeSetpoints.Intake: HandleIntake(hasCoral, hasAlgae, shooterHasAlgae); break;
                case ReefscapeSetpoints.Place: HandlePlace(shooterHasCoral); break;
                case ReefscapeSetpoints.L1: HandleL1(shooterHasCoral); break;
                case ReefscapeSetpoints.Stack: HandleStack(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L2: HandleL2(shooterHasCoral); break;
                case ReefscapeSetpoints.LowAlgae: HandleLowAlgae(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L3: HandleL3(shooterHasCoral); break;
                case ReefscapeSetpoints.HighAlgae: HandleHighAlgae(hasAlgae, shooterHasCoral); break;
                case ReefscapeSetpoints.L4: HandleL4(shooterHasCoral); break;
                case ReefscapeSetpoints.Processor: HandleProcessor(shooterHasAlgae); break;
                case ReefscapeSetpoints.Barge: HandleBarge(shooterHasAlgae); break;
                case ReefscapeSetpoints.RobotSpecial: froggyLolli = !froggyLolli; break;
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

            SetRollerSpeeds(0, shooterHasCoral || shooterHasAlgae ? 0 : shooterAnimationWheelSpeeds);
        }

        private void HandleIntake(bool hasCoral, bool hasAlgae, bool shooterHasAlgae)
        {
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1 && !hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
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
            else if (CurrentRobotMode == ReefscapeRobotMode.Coral && !hasCoral && !shooterHasAlgae && CurrentIntakeMode != ReefscapeIntakeMode.L1)
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
        }

        private void HandlePlace(bool shooterHasCoral)
        {
            if (stillInPlaceState) return;

            if (shooterHasCoral && LastSetpoint == ReefscapeSetpoints.L4) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL4 : backL4Scored);

            HandlePlaceScoring();
        }

        private void HandleL1(bool shooterHasCoral)
        {
            if (!shooterHasCoral) SetSetpoint(froggyCoralPlace);
            else if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(eeL1);
            frogState = FroggyState.Stow;

            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
            foreach (var col in shooterCollidersForAlgae) col.enabled = true;
        }

        private void HandleStack(bool hasAlgae, bool shooterHasCoral)
        {
            if (shooterHasCoral || hasAlgae) return;

            if (DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(lollipopIntake);
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
                SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL2 : backL2);
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
            if (shooterHasCoral || hasAlgae) return;

            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontLowAlgae : backLowAlgae);
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
                SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL3 : backL3);
            }
            _algaeController.RequestIntake(funnelCoralIntake, false);
            _coralController.RequestIntake(froggyCoralIntake, false);
            _coralController.RequestIntake(shooterAlgaeIntake, false);
            _algaeController.RequestIntake(froggyAlgaeIntake, false);
        }

        private void HandleHighAlgae(bool hasAlgae, bool shooterHasCoral)
        {
            frogState = FroggyState.Stow;
            if (shooterHasCoral || hasAlgae) return;

            if (!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontHighAlgae : backHighAlgae);
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
            if (shooterHasCoral) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL4 : backL4);
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
            if (shooterHasAlgae) SetSetpoint(barge);
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

            if (TryShootFroggyCoral()) { }
            else if (TryReleaseShooterAlgae()) { }
            else if (TryScoreL4Coral()) { }
            else if (TryScoreL1Coral()) { }
            else if (TryScoreDefaultCoral()) { }

            stillInPlaceState = true;
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
                !_coralController.HasPiece() ||
                _coralController.currentStateNum == shooterCoralStowState.stateNum && _coralController.atTarget)
            {
                return false;
            }

            StartCoroutine(ShootFroggyCoral());
            return true;
        }

        private bool TryReleaseShooterAlgae()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Algae && _coralController.atTarget) || !_algaeController.HasPiece())
            {
                return false;
            }

            if (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && LastSetpoint == ReefscapeSetpoints.Barge)
            {
                frogState = FroggyState.Stow;
                foreach (var col in shooterCollidersForAlgae) col.enabled = false;
                _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 0, 1.5f));
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
            if ((CurrentRobotMode != ReefscapeRobotMode.Coral && _algaeController.atTarget) || LastSetpoint != ReefscapeSetpoints.L4)
            {
                return false;
            }

            frogState = FroggyState.Stow;
            foreach (var col in shooterCollidersForAlgae) col.enabled = false;

            var facingReef = IsFacingReef(GetClosestReef());
            if (facingReef)
            {
                _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, -6));
            }
            else
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 5), 0.3f, 3);
            }
            SetRollerSpeeds(0, facingReef ? -shooterAnimationWheelSpeeds : shooterAnimationWheelSpeeds);
            return true;
        }

        private bool TryScoreL1Coral()
        {
            if ((CurrentRobotMode != ReefscapeRobotMode.Coral && !_algaeController.atTarget) ||
                LastSetpoint != ReefscapeSetpoints.L1 || CurrentIntakeMode != ReefscapeIntakeMode.Normal)
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
            var hasCoral = _coralController.HasPiece();
            var hasAlgae = _algaeController.HasPiece();
            var isFroggyMode = CurrentIntakeMode == ReefscapeIntakeMode.L1;
            var isStationMode = !isFroggyMode;

            var froggyIntaking = isFroggyMode && Mathf.Abs(_froggyWheels) > 1e-6;
            var froggyOuttaking = Time.time < _froggyOuttakeAudioUntil;
            PlayOrStop(froggyAudioSource, (froggyIntaking || froggyOuttaking) && isFroggyMode);

            var funnelIntaking = isStationMode && IntakeAction.IsPressed() && !hasCoral && !hasAlgae;
            var funnelOuttaking = isStationMode && Time.time < _outtakeAudioUntil;
            PlayOrStop(funnelAudioSource, (funnelIntaking || funnelOuttaking) && isStationMode);

            PlayOrStop(endEffectorAudioSource, _isIntaking && !hasCoral && !hasAlgae);
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

        private Vector3 GetClosestReef()
        {
            return DistanceToReef(_blueReef) < DistanceToReef(_redReef) ? _blueReef : _redReef;
        }

        private bool IsFacingReef(Vector3 reefPos)
        {
            var toReef = (reefPos - transform.position).normalized;
            return Vector3.Dot(transform.forward.normalized, toReef) > 0.0f;
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
