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

namespace Prefabs.Reefscape.Robots.Mods.Spartans._971
{
    public class Spartans: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
            [SerializeField] private GenericJoint arm, wrist, intake, intakeFlap, climber;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID;
            [SerializeField] private PidConstants wristPID, climberPID, intakePID, intakeFlapPID;

        [Header("Setpoints")]
        [SerializeField] private SpartansSetpoint stow;
            [SerializeField] private SpartansSetpoint groundIntake1, groundIntake2, groundIntake3;
            [SerializeField] private SpartansSetpoint hpIntakeFront, hpIntakeBack;
            [SerializeField] private SpartansSetpoint l1Front, l1Back, l2Front, l2Back, l3Front, l3Back, l4Front, l4Back;
            [SerializeField] private SpartansSetpoint groundAlgae, lollipop, lowAlgaeFront, lowAlgaeBack, highAlgaeFront, highAlgaeBack, proc, bargeFront, bargeBack;
            [SerializeField] private SpartansSetpoint climbPrep, climbClimb;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
            [SerializeField] private ReefscapeGamePieceIntake groundCoralIntake, hpCoralIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState algaeStowState;
            [SerializeField] private GamePieceState groundIntakeState, endEffectorState;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
        [SerializeField] private AudioClip funnelCloseAudio;
        [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController, _algaeController;

        private float _elevatorTargetHeight, _armTargetAngle, _wristTargetAngle, _intakeTargetAngle, _climberTargetAngle;
        private float _intakeFlapTargetAngle = 0;

        private LayerMask coralMask;
        private bool canClack;
        
        private bool _isRetractingFromGround;
        private SpartansSetpoint _pendingSetpoint;
        private SpartansSetpoint _retractFromWaypoint;
        
        private Vector3 _blueReef = new Vector3(-4.298872f, 0, 0);
        private Vector3 _redReef = new Vector3(4.298872f, 0, 0);
        
        private Vector3 _blueProcHp, _blueNonProcHp;
        private Vector3 _redProcHp, _redNonProcHp;

        private Vector3 _closestHp;
        
        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPID);
            wrist.SetPid(wristPID);
            intake.SetPid(intakePID);
            intakeFlap.SetPid(intakeFlapPID);
            climber.SetPid(climberPID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _wristTargetAngle = stow.wristAngle;
            _climberTargetAngle = stow.climberAngle;
            _intakeTargetAngle = stow.intakeAngle;
            _intakeFlapTargetAngle = 0;
            
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            _coralController.gamePieceStates = new[]
            {
                groundIntakeState,
                endEffectorState
            };
            _coralController.intakes.Add(groundCoralIntake);
            _coralController.intakes.Add(hpCoralIntake);
            
            RobotGamePieceController.SetPreload(endEffectorState);

            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;
            
            _blueProcHp = GameObject.Find("Coral Station").transform.position;
            _blueNonProcHp = GameObject.Find("Coral Station (1)").transform.position;
            _redProcHp = GameObject.Find("Coral Station (3)").transform.position;
            _redNonProcHp = GameObject.Find("Coral Station (2)").transform.position;
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPID);
            wrist.UpdatePid(wristPID);
            intake.UpdatePid(intakePID);
            intakeFlap.UpdatePid(intakeFlapPID);
            climber.UpdatePid(climberPID);
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            bool hasAlgae = _algaeController.HasPiece();
            
            if (_isRetractingFromGround)
            {
                GoFromGround();
                if (SuperstructureAtSetpoint(stow))
                {
                    _isRetractingFromGround = false;
                    SetSetpoint(_pendingSetpoint);
                }
                UpdateSetpoints();
                UpdateAudio();
                return;
            }
            
            _coralController.RequestIntake(hpCoralIntake, IntakeAction.IsPressed() && (SuperstructureAtSetpoint(hpIntakeFront) || SuperstructureAtSetpoint(hpIntakeBack)));
            _coralController.RequestIntake(groundCoralIntake, IntakeAction.IsPressed() && SuperstructureAtSetpoint(groundIntake3));
            _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && IsAlgaeSetpoint());

            if (SuperstructureAtSetpoint(groundIntake3))
            {
                if (!hasCoral)
                {
                    _coralController.SetTargetState(groundIntakeState);
                }
                else
                {
                    if (SuperstructureAtSetpoint(groundIntake3) && _coralController.atTarget &&
                        _coralController.currentStateNum == groundIntakeState.stateNum)
                    {
                        _coralController.SetTargetState(endEffectorState);
                    }
                }
            }
            else
            {
                _coralController.SetTargetState(endEffectorState);
            }

            _algaeController.SetTargetState(algaeStowState);
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: RequestSetpoint(stow); break;
                case ReefscapeSetpoints.Intake:
                    if (hasCoral || hasAlgae) break;
                    
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae)
                    {
                        if (!IsAlgaeSetpoint()) RequestSetpoint(groundAlgae);
                    }
                    else
                    {
                        if (CurrentCoralStationMode.DropType == DropType.Ground)
                        {
                            GoToGround();
                        }
                        else
                        {
                            RequestSetpoint(IsFacingHp() ? hpIntakeBack : hpIntakeFront);
                        }
                    }

                    break;
                
                case ReefscapeSetpoints.Processor: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Stack: RequestSetpoint(lollipop); break;
                case ReefscapeSetpoints.Barge: SetState(ReefscapeSetpoints.Stow); break;
                
                case ReefscapeSetpoints.LowAlgae: RequestSetpoint(IsFacing(GetClosestReef()) ? lowAlgaeFront : lowAlgaeBack); break;
                case ReefscapeSetpoints.HighAlgae: RequestSetpoint(IsFacing(GetClosestReef()) ? highAlgaeFront : highAlgaeBack); break;
                
                case ReefscapeSetpoints.L1: if (_coralController.atTarget) RequestSetpoint(l1Front); break;
                case ReefscapeSetpoints.L2:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l2Front : l2Back);
                    } 
                    break;
                case ReefscapeSetpoints.L3:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l3Front : l3Back);
                    } 
                    break;
                case ReefscapeSetpoints.L4:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l4Front : l4Back);
                    } 
                    break;
                
                case ReefscapeSetpoints.Climb: RequestSetpoint(climbPrep); break;
                case ReefscapeSetpoints.Climbed: RequestSetpoint(climbClimb); break;
                
                case ReefscapeSetpoints.Place: PlacePiece(); break;
                
                case ReefscapeSetpoints.RobotSpecial: 
                    CurrentCoralStationMode.DropType = (CurrentCoralStationMode.DropType == DropType.Ground ? DropType.Station : DropType.Ground);
                    CurrentCoralStationMode.DropOrientation = (CurrentCoralStationMode.DropOrientation == DropOrientation.Horizontal ? DropOrientation.Vertical : DropOrientation.Horizontal); 
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
            
            UpdateSetpoints();
            UpdateAudio();
        }

        #region Actuators & Setpoints
        
        private void SetSetpoint(SpartansSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _armTargetAngle = setpoint.armAngle;
            _wristTargetAngle = setpoint.wristAngle;
            _intakeTargetAngle = setpoint.intakeAngle;
            _climberTargetAngle = setpoint.climberAngle;
        }

        private float MetersToInches(float met)
        {
            return (float)(met * 39.3700787402);
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(MetersToInches(_elevatorTargetHeight));
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.Y)
                .useCustomStartingOffset(70);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X)
                .noWrap(100)
                .useCustomStartingOffset(0);
            wrist.SetTargetAngle(_wristTargetAngle - 90).withAxis(JointAxis.X)
                .noWrap(180)
                .useCustomStartingOffset(0);
            intake.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.Y)
                .useCustomStartingOffset(0);
            intakeFlap.SetTargetAngle(_intakeFlapTargetAngle).withAxis(JointAxis.X)
                .useCustomStartingOffset(0);
            
            print("Flap target at " + _intakeFlapTargetAngle + " Current Angle " + intakeFlap.GetSingleAxisAngle(JointAxis.X));
        }

        private void GoToGround()
        {
            if (SuperstructureAtSetpoint(groundIntake3)) return;
            
            if (SuperstructureAtSetpoint(groundIntake2))
            {
                SetSetpoint(groundIntake3);
            }
            else if (SuperstructureAtSetpoint(groundIntake1))
            {
                SetSetpoint(groundIntake2);
            }
            else if (!CurrentSetpointIs(groundIntake2) && !CurrentSetpointIs(groundIntake3))
            {
                SetSetpoint(groundIntake1);
            }
        }
        
        private void GoFromGround()
        {
            if (CurrentSetpointIs(stow) && SuperstructureAtSetpoint(stow)) return;

            if (SuperstructureAtSetpoint(groundIntake3) && CurrentSetpointIs(groundIntake3))
            {
                SetSetpoint(groundIntake2);
            }
            else if (SuperstructureAtSetpoint(groundIntake2) && CurrentSetpointIs(groundIntake2))
            {
                SetSetpoint(groundIntake1);
            }
            else if (SuperstructureAtSetpoint(groundIntake1) && CurrentSetpointIs(groundIntake1))
            {
                SetSetpoint(stow);
            }
        }

        private bool SuperstructureAtSetpoint(SpartansSetpoint setpoint)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), MetersToInches(setpoint.elevatorHeight), 2f);
            
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X) - 283, setpoint.armAngle, 2f);
            bool wristAtSetpoint = Utils.InAngularRange(wrist.GetSingleAxisAngle(JointAxis.X) - 355, setpoint.wristAngle - 90, 2f);
            bool intakeAtSetpoint = Utils.InAngularRange(intake.GetSingleAxisAngle(JointAxis.Y) + (float)71.4, setpoint.intakeAngle, 2f);
            
            return elevatorAtSetpoint && intakeAtSetpoint && wristAtSetpoint && armAtSetpoint;
        }

        private bool CurrentSetpointIs(SpartansSetpoint setpoint)
        {
            return
                _elevatorTargetHeight == setpoint.elevatorHeight &&
                _armTargetAngle == setpoint.armAngle &&
                _wristTargetAngle == setpoint.wristAngle &&
                _intakeTargetAngle == setpoint.intakeAngle;
        }

        private bool IsAlgaeSetpoint()
        {
            return CurrentSetpointIs(groundAlgae) ||
                   CurrentSetpointIs(lollipop) ||
                   CurrentSetpointIs(lowAlgaeFront) ||
                   CurrentSetpointIs(highAlgaeFront) ||
                   CurrentSetpointIs(lowAlgaeBack) ||
                   CurrentSetpointIs(highAlgaeBack);
        }
        
        private bool IsInGroundSequence()
        {
            return CurrentSetpointIs(groundIntake1) || 
                   CurrentSetpointIs(groundIntake2) || 
                   CurrentSetpointIs(groundIntake3);
        }

        private void RequestSetpoint(SpartansSetpoint setpoint)
        {
            if (IsInGroundSequence() && CurrentSetpoint != ReefscapeSetpoints.Intake)
            {
                _isRetractingFromGround = true;
                _pendingSetpoint = setpoint;

                if (SuperstructureAtSetpoint(groundIntake3))
                    SetSetpoint(groundIntake3);
                else if (SuperstructureAtSetpoint(groundIntake2))
                    SetSetpoint(groundIntake2);
                else
                    SetSetpoint(groundIntake1);
            }
            else
            {
                SetSetpoint(setpoint);
            }
        }
        
        #endregion
        

        #region Logic Helpers
        
        private float DistanceTo(Vector3 itemPos)
        {
            return Mathf.Sqrt(Mathf.Pow(transform.position.x - itemPos.x, 2) + Mathf.Pow(transform.position.z - itemPos.z, 2));
        }
    
        private Vector3 GetClosestReef()
        {
            print("Facing " + (DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef) + " with distance of " + DistanceTo(DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef));
            return DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef;
        }

        private bool IsFacing(Vector3 itemPos)
        {
            var toReefVector = (itemPos - transform.position).normalized;
            var robotForwardVector = transform.forward.normalized;
            var angle = Vector3.Dot(robotForwardVector, toReefVector);
            return angle > 0.0f;
        }

        private Vector3 GetClosestHp(Vector3 procHp, Vector3 nonProcHp)
        {
            return DistanceTo(procHp) < DistanceTo(nonProcHp) ? procHp : nonProcHp;
        }
        
        private bool IsFacingHp()
        {
            if (GetClosestReef() == _blueReef)
            {
                _closestHp = GetClosestHp(_blueProcHp, _blueNonProcHp);
            }
            else
            {
                _closestHp = GetClosestHp(_redProcHp, _redNonProcHp);
            }

            return IsFacing(_closestHp);
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

            if (((IntakeAction.IsPressed() && !_coralController.HasPiece() && !_coralController.HasPiece()) ||
                 OuttakeAction.IsPressed()) &&
                !rollerSource.isPlaying)
            {
                rollerSource.Play();
            }
            else if (!IntakeAction.IsPressed() && !OuttakeAction.IsPressed() && rollerSource.isPlaying)
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

        private void PlacePiece()
        {
            if (LastSetpoint == ReefscapeSetpoints.L1)
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, -5.5f), 0.5f, 0.5f);
            }
            else
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3), 0.5f, 0.5f);
            }
        }
        
        #endregion
    }
}