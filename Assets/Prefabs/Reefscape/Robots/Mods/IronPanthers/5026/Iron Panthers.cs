using Games.Reefscape.Enums;
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

namespace Prefabs.Reefscape.Robots.Mods.IronPanthers._5026
{
    public class IronPanthers: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm, climber, armSensor;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID, climberPID, armSensorPID;

        [Header("Setpoints")]
        [SerializeField] private IronPanthersSetpoint stow, intake, l1, l2, l3, l4Prep, l4;
        [SerializeField] private IronPanthersSetpoint lowDescore, highDescore;
        [SerializeField] private float minimumElevatorHeightForSwingAround = 31;
        [SerializeField] private float climberStow, climberClimb, climberTop = 20;
        [SerializeField] private float armSensorL4 = -64;

        // From the team's Superstructure.java (elevator units x 1.14 in, pivot + 106 deg). See Setpoints/ for the rest.
        [Header("Descore Rollers")]
        [SerializeField] private GenericRoller[] descoreRollers;
        [SerializeField] private float descoreRollerSpeed = -7000;
        [Tooltip("What a spinning compliant wheel does to the ball: rigid contact alone only spins it in place.")]
        [SerializeField] private float descoreForce = 60;
        [SerializeField] private float descoreReach = 0.15f;
        [SerializeField] private float descoreBallSpeed = 2.5f;
        [Tooltip("Sideways from a branch to the algae between the two branches of a face, inches.")]
        [SerializeField] private float descoreAlignShift = 6.5f;
        [SerializeField] private float alignDescore = 0f;
        [Tooltip("The arm waits up here, inside the bumpers and clear of the branches, until the robot is square at the face. Then it comes down between them onto the ball.")]
        [SerializeField] private float descoreWaitArm = 150f;
        [SerializeField] private float descoreSquareAngle = 4f;
        [SerializeField] private float descoreHomeDistance = 1.4f;

        [Header("One-Way Latch")]
        [Tooltip("Pin of the hook plates; its children are the hook's meshes and solid colliders.")]
        [SerializeField] private Transform latch;
        [Tooltip("Strip along the hook's curved top: a cage post here cams the hook open.")]
        [SerializeField] private BoxCollider latchCamStrip;
        [SerializeField] private float latchOpenAngle = 40;
        [SerializeField] private float latchSpeed = 360;
        [SerializeField] private PidConstants climberPullPID;
        [SerializeField] private float climbElevator = 14.8f, climbArm = 351;
        [SerializeField] private AudioClip latchClip;

        [Header("Superstructure Sequencing")]
        [SerializeField] private float armSensorL3 = -24;
        [SerializeField] private float topHeight = 35.3f;
        [SerializeField] private float intakeSideHeight = 14.8f;
        [SerializeField] private float intakeSideArm = 146;
        [SerializeField] private float topArm = 27;
        [SerializeField] private float topLeanHeight = 5.7f;
        [SerializeField] private float l3SetupArm = 27;
        [SerializeField] private float armOverAngle = 46;
        [SerializeField] private float armScoreSideAngle = 196;

        [Header("Auto Align Offsets (m, from ApproachReef.java)")]
        [SerializeField] private float alignBaseOffset = 9.6f;
        [SerializeField] private float alignL1 = 0f;
        [SerializeField] private float alignL2 = 0f;
        [SerializeField] private float alignL3 = 0.07f;
        [SerializeField] private float alignL4Prep = 0.5f;
        [SerializeField] private float alignL4 = 0.09f;
        [Tooltip("Rigidbody drag: free driving reaches the team's 4.5 m/s; auto align runs a position loop that holds still.")]
        [SerializeField] private float driveDrag = 1.7f, alignDrag = 3f;

        [Header("Eject (force, max speed m/s)")]
        [SerializeField] private float ejectL1Force = 0.15f;
        [SerializeField] private float ejectL1Speed = 0.2f;
        [SerializeField] private float ejectL2Force = 1.5f;
        [SerializeField] private float ejectL2Speed = 2f;
        [SerializeField] private float ejectL3Force = 1.5f;
        [SerializeField] private float ejectL3Speed = 2f;
        [SerializeField] private float ejectL4Force = 1.2f;
        [SerializeField] private float ejectL4Speed = 1f;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState coralStowState;
        [SerializeField] private GamePieceState coralEjectState;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
        [SerializeField] private AudioClip funnelCloseAudio;
        [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private float _elevatorTargetHeight, _armTargetAngle, _armSensorTargetAngle, _climberTargetAngle;

        private LayerMask coralMask;
        private bool canClack;

        private ReefscapeAutoAlign _autoAlign;
        private Rigidbody _rb;
        private Transform _reef;
        private bool _placed, _ejecting, _l3SetupDone;
        private float _placeStartTime, _releaseTime, _latchAngle;
        private readonly Collider[] _latchHits = new Collider[16];
        private readonly Collider[] _algaeHits = new Collider[8];
        private int _algaeMask;

        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPID);
            armSensor.SetPid(armSensorPID);
			climber.SetPid(climberPID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _climberTargetAngle = climberStow;
            _armSensorTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));

            _coralController.gamePieceStates = new[]
            {
                coralStowState,
                coralEjectState
            };
            _coralController.intakes.Add(coralIntake);
            
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);

            coralMask = LayerMask.GetMask("Coral");
            _algaeMask = LayerMask.GetMask("Algae");
            canClack = true;

            // Auto tensor on a light arm twists off its hinge when a crash stops the robot in one step: 5 deg at the
            // framework's iterations. Same fix as 971.
            var stabilizer = GetComponent<JointStackStabilizer>();
            stabilizer.solverIterations = 200;
            stabilizer.solverVelocityIterations = 75;

            _autoAlign = GetComponent<ReefscapeAutoAlign>();
            _rb = GetComponent<Rigidbody>();
            _reef = GameObject.Find(Alliance == Alliance.Blue ? "BlueReef" : "RedReef").transform;
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPID);
            climber.UpdatePid(CurrentSetpoint == ReefscapeSetpoints.Climbed ? climberPullPID : climberPID);
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            
            _coralController.SetTargetState(_ejecting ? coralEjectState : coralStowState);
            _coralController.RequestIntake(coralIntake, SuperstructureAtSetpoint(intake) && IntakeAction.IsPressed() && !hasCoral);
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: SetSetpoint(stow); SetClimberAngle(climberStow); break;
                case ReefscapeSetpoints.Intake: SetSetpoint(intake); break;
                
                case ReefscapeSetpoints.Processor: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Stack: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Barge: SetState(ReefscapeSetpoints.Stow); break;
                
                case ReefscapeSetpoints.LowAlgae: SetDescore(lowDescore); break;
                case ReefscapeSetpoints.HighAlgae: SetDescore(highDescore); break;
                
                case ReefscapeSetpoints.L1: if (_coralController.atTarget) SetSetpoint(l1); break;
                case ReefscapeSetpoints.L2: if (_coralController.atTarget) SetSetpoint(l2); break;
                case ReefscapeSetpoints.L3: if (_coralController.atTarget) SetL3(); break;
                case ReefscapeSetpoints.L4: if (_coralController.atTarget) SetSetpoint(l4Prep); break;
                
                case ReefscapeSetpoints.Climb: SetClimbPose(); SetClimberAngle(climberClimb); break;
                case ReefscapeSetpoints.Climbed: SetClimbPose(); SetClimberAngle(climberTop); break;
                
                case ReefscapeSetpoints.Place: PlacePiece(); break;
            }
            
            
            if (CurrentSetpoint != ReefscapeSetpoints.Place) _placed = false;
            if (!_coralController.HasPiece()) _ejecting = false;

            var level = CurrentSetpoint == ReefscapeSetpoints.Place ? LastSetpoint : CurrentSetpoint;
            SetArmSensorAngle(level == ReefscapeSetpoints.L4 ? armSensorL4 : level == ReefscapeSetpoints.L3 ? armSensorL3 : 0);
            UpdateAlignOffset(level);
            UpdateDrag();

            UpdateDescore();
            UpdateLatch();
            UpdateSetpoints();
            UpdateAudio();
        }

        private void SetDescore(IronPanthersSetpoint setpoint)
        {
            SetSetpoint(setpoint);
            var fromReef = Vector3.ProjectOnPlane(transform.position - _reef.position, Vector3.up);
            var home = fromReef.magnitude < descoreHomeDistance && Vector3.Angle(transform.forward, fromReef) < descoreSquareAngle;
            if (!home && _armTargetAngle == setpoint.armAngle) _armTargetAngle = descoreWaitArm;
        }

        // Real DESCORE: the end effector's wheels run while the arm is out behind the robot, which auto align backs onto the
        // algae between two branches. A ball the wheels touch gets driven off the reef.
        private void UpdateDescore()
        {
            var descoring = CurrentSetpoint is ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae;
            _autoAlign.enableForwardAlign = !descoring;
            foreach (var roller in descoreRollers) roller.SetAngularVelocity(descoring ? descoreRollerSpeed : 0f);
            if (!descoring) return;

            Rigidbody last = null;
            foreach (var roller in descoreRollers)
            {
                var hits = Physics.OverlapSphereNonAlloc(roller.transform.position, descoreReach, _algaeHits, _algaeMask, QueryTriggerInteraction.Collide);
                for (var i = 0; i < hits; i++)
                {
                    var ball = _algaeHits[i].attachedRigidbody;
                    if (!ball || ball == last || ball.velocity.magnitude > descoreBallSpeed) continue;
                    last = ball;
                    var off = Vector3.ProjectOnPlane(ball.position - _reef.position, Vector3.up).normalized;
                    ball.AddForce((off + Vector3.up * 0.5f).normalized * descoreForce);
                }
            }
        }

        // The hook is free one way only: a post on its curved top cams it open about its pin, and with nothing there the
        // spring brings it back behind the post. Nothing holds the post but the hook's shape against its stop.
        private void UpdateLatch()
        {
            // Tested where the strip sits with the hook shut: the hook stays open only while a shut hook would hit the post.
            var strip = latchCamStrip.transform;
            var shut = latch.parent.rotation * strip.localRotation;
            var centre = latch.parent.TransformPoint(latch.localPosition + strip.localPosition + strip.localRotation * latchCamStrip.center);
            var hits = Physics.OverlapBoxNonAlloc(centre, Vector3.Scale(latchCamStrip.size, strip.lossyScale) / 2, _latchHits, shut, ~0, QueryTriggerInteraction.Ignore);
            var cammed = false;
            for (var i = 0; i < hits && !cammed; i++) cammed = _latchHits[i].CompareTag("Cage");

            var was = _latchAngle;
            _latchAngle = Mathf.MoveTowards(_latchAngle, cammed ? latchOpenAngle : 0f, latchSpeed * Time.fixedDeltaTime);
            if (was > 0f && _latchAngle == 0f && funnelCloseSource) funnelCloseSource.PlayOneShot(latchClip);
            latch.localRotation = Quaternion.AngleAxis(-_latchAngle, Vector3.forward);
        }

        #region Actuators & Setpoints

        // The team's PREVENT_TIPPING route for swinging over the top: elevator to the top while the arm leaves the
        // funnel side, down to the intake-side height while it sweeps across, then up to the target.
        private void SetSetpoint(IronPanthersSetpoint setpoint)
        {
            var armNow = Signed(arm.GetSingleAxisAngle(JointAxis.X));
            var armWanted = Signed(setpoint.armAngle);
            var isOver = armNow > armOverAngle;
            var wantOver = armWanted > armOverAngle;

            if (wantOver && !isOver)
            {
                // TOP: past 5 units the pivot leans to -79 so the coral clears the funnel's back plate on the way up.
                _elevatorTargetHeight = topHeight;
                var height = elevator.GetElevatorHeight();
                if (height > minimumElevatorHeightForSwingAround) _armTargetAngle = setpoint.armAngle;
                else if (height > topLeanHeight) _armTargetAngle = topArm;
            }
            else if (!wantOver && isOver)
            {
                if (armNow > intakeSideArm + 5)
                {
                    _armTargetAngle = intakeSideArm;
                    _elevatorTargetHeight = intakeSideHeight;
                }
                else
                {
                    _elevatorTargetHeight = topHeight;
                    if (elevator.GetElevatorHeight() > 5) _armTargetAngle = topArm;
                }
            }
            else if (wantOver)
            {
                _armTargetAngle = setpoint.armAngle;
                _elevatorTargetHeight = armWanted > armScoreSideAngle && armNow < armScoreSideAngle
                    ? intakeSideHeight
                    : setpoint.elevatorHeight;
            }
            else
            {
                _elevatorTargetHeight = setpoint.elevatorHeight;
                _armTargetAngle = setpoint.armAngle;
            }
        }

        // SETUP_L3: the arm leans clear of the funnel on the way up, swings up to clear the branch at height, then
        // SCORE_L3 brings the coral down onto it.
        private void SetL3()
        {
            if (!ElevatorAtHeight(l3.elevatorHeight) && Signed(arm.GetSingleAxisAngle(JointAxis.X)) <= armOverAngle)
            {
                _elevatorTargetHeight = l3.elevatorHeight;
                if (elevator.GetElevatorHeight() > topLeanHeight) _armTargetAngle = topArm; // TOP, as for L4
                _l3SetupDone = false;
                return;
            }

            if (!_l3SetupDone)
            {
                SetSetpoint(l3);
                _armTargetAngle = l3SetupArm;
                _l3SetupDone = ArmAtAngle(l3SetupArm);
                return;
            }

            SetSetpoint(l3);
        }

        private static float Signed(float angle)
        {
            return angle > 270 ? angle - 360 : angle;
        }

        // Real CLIMB: elevator 13 units, pivot -115.
        private void SetClimbPose()
        {
            _elevatorTargetHeight = climbElevator;
            _armTargetAngle = climbArm;
        }

        private void SetClimberAngle(float climberAngle)
        {
            _climberTargetAngle = climberAngle;
        }

        private void SetArmSensorAngle(float armSensorAngle)
        {
            _armSensorTargetAngle = armSensorAngle;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X);
            armSensor.SetTargetAngle(_armSensorTargetAngle).withAxis(JointAxis.X);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X)
                .noWrap(270);
        }
        
        #endregion
        

        #region Logic Helpers

        private bool SuperstructureAtSetpoint(IronPanthersSetpoint targetSetpoint)
        {
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), targetSetpoint.armAngle, 5f);
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetSetpoint.elevatorHeight, 5f);

            return armAtSetpoint && elevatorAtSetpoint;
        }
        
        private bool ElevatorAtHeight(float targetHeight)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetHeight, 5f);

            return elevatorAtSetpoint;
        }
        
        private bool ArmAtAngle(float targetAngle)
        {
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), targetAngle, 5f);

            return armAtSetpoint;
        }

        private void UpdateAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
                }

                return;
            }

            var descoring = CurrentSetpoint is ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae;
            if (((IntakeAction.IsPressed() && !_coralController.HasPiece() && !_coralController.HasPiece()) ||
                 OuttakeAction.IsPressed() || descoring) &&
                !rollerSource.isPlaying)
            {
                rollerSource.Play();
            }
            else if (!IntakeAction.IsPressed() && !OuttakeAction.IsPressed() && !descoring && rollerSource.isPlaying)
            {
                rollerSource.Stop();
            }
            else if (IntakeAction.IsPressed() && (_coralController.HasPiece()))
            {
                rollerSource.Stop();
            }

            var a = soundDetector.OverlapBox(coralMask);
            if (a.Length > 0)
            {
                if (canClack && !funnelCloseSource.isPlaying)
                {
                    funnelCloseSource.Play();
                    canClack = false;
                }
            }
            else
            {
                canClack = true;
            }
        }

        // Held after scoring too, so a robot still holding align doesn't creep into the coral it just placed.
        private void UpdateAlignOffset(ReefscapeSetpoints level)
        {
            float offset;
            switch (level)
            {
                case ReefscapeSetpoints.L1: offset = alignL1; break;
                case ReefscapeSetpoints.L2: offset = alignL2; break;
                case ReefscapeSetpoints.L3: offset = alignL3; break;
                case ReefscapeSetpoints.LowAlgae:
                case ReefscapeSetpoints.HighAlgae:
                    offset = alignDescore;
                    if (AutoAlignLeftAction.IsPressed()) _autoAlign.offset.x = descoreAlignShift;
                    else if (AutoAlignRightAction.IsPressed()) _autoAlign.offset.x = -descoreAlignShift;
                    _autoAlign.offset.z = alignBaseOffset - offset / 0.0254f;
                    return;
                case ReefscapeSetpoints.L4:
                    offset = CurrentSetpoint == ReefscapeSetpoints.Place || LevelReached(l4Prep) ? alignL4 : alignL4Prep;
                    break;
                default:
                    if (AutoAlignLeftAction.IsPressed() || AutoAlignRightAction.IsPressed()) return;
                    offset = alignL2;
                    break;
            }
            _autoAlign.offset.z = alignBaseOffset - offset / 0.0254f;
            _autoAlign.offset.x = 0f;
        }

        private bool LevelReached(IronPanthersSetpoint setpoint)
        {
            return Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), setpoint.armAngle, 3f) &&
                   Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 2f);
        }

        private IronPanthersSetpoint LevelSetpoint(ReefscapeSetpoints level)
        {
            return level switch
            {
                ReefscapeSetpoints.L1 => l1,
                ReefscapeSetpoints.L2 => l2,
                ReefscapeSetpoints.L3 => l3,
                ReefscapeSetpoints.L4 => l4Prep,
                _ => null
            };
        }

        private void UpdateDrag()
        {
            _rb.drag = AutoAlignLeftAction.IsPressed() || AutoAlignRightAction.IsPressed() ? alignDrag : driveDrag;
        }

        // Rollers are one motor: L1-L3 carry the coral on out the far side (+2 V to +2.4 V), everything else ejects back
        // the way it came (-8 V). SCORE_L4 pulls down on the branch while ejecting. 0.2 s (L4) or 0.5 s later: back to intake.
        private void PlacePiece()
        {
            var level = LastSetpoint;
            if (level == ReefscapeSetpoints.L4) SetSetpoint(l4);

            if (!_placed)
            {
                _placed = true;
                _placeStartTime = Time.time;
                _releaseTime = -1;
            }

            if (_releaseTime < 0)
            {
                var setpoint = LevelSetpoint(level);
                if (setpoint != null && !LevelReached(setpoint) && Time.time - _placeStartTime < 0.6f) return;

                // L1-L3: the rollers carry the coral straight out along its axis until its tail leaves them.
                if (level is ReefscapeSetpoints.L1 or ReefscapeSetpoints.L2 or ReefscapeSetpoints.L3)
                {
                    _ejecting = true;
                    if (_coralController.wasMovingTo != coralEjectState.name.Trim().ToLower() || !_coralController.atTarget) return;
                }

                switch (level)
                {
                    case ReefscapeSetpoints.L1:
                        _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, ejectL1Force), 0.5f, ejectL1Speed);
                        break;
                    case ReefscapeSetpoints.L2:
                        _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, ejectL2Force), 0.5f, ejectL2Speed);
                        break;
                    case ReefscapeSetpoints.L3:
                        _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, ejectL3Force), 0.5f, ejectL3Speed);
                        break;
                    default:
                        _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, -ejectL4Force), 0.5f, ejectL4Speed);
                        break;
                }
                _releaseTime = Time.time;
            }

            if (Time.time - _releaseTime > (level == ReefscapeSetpoints.L4 ? 0.2f : 0.5f)) SetState(ReefscapeSetpoints.Stow);
        }
        
        #endregion
    }
}