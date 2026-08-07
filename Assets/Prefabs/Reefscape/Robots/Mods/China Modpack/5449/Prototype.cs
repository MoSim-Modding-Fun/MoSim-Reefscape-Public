using System.Collections;
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

namespace Prefabs.Reefscape.Robots.Mods.PrototypeMod._5449
{
    public class Prototype: ReefscapeRobotBase
    {
        [Header("Animation Joints")]
        [SerializeField] private GenericAnimationJoint[] endEffectorRollers;
        [SerializeField] private float eeRollerSpeeds = 180;
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm;
        [SerializeField] private GenericJoint funnel;
        [SerializeField] private GenericJoint climber;
        
        [Header("End Effector Rollers + Colliders")]
        [SerializeField] private GenericRoller[]  rollers;
        [SerializeField] private Collider[] EEcolliders;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID;
        [SerializeField] private PidConstants funnelPID;
        [SerializeField] private PidConstants climberPID;

        [Header("Coral Setpoints")]
        [SerializeField] private SingleEditableFloat lowerDistance;
        [SerializeField] private PrototypeSetpoint stow;
        [SerializeField] private PrototypeSetpoint algaeStow;
        [SerializeField] private PrototypeSetpoint stowLow;
        [SerializeField] private PrototypeSetpoint algaeStowLow;
        [SerializeField] private PrototypeSetpoint intake;
        [SerializeField] private PrototypeSetpoint intakeLow;
        [SerializeField] private PrototypeSetpoint l1;
        [SerializeField] private PrototypeSetpoint l1Place;

        [SerializeField] private PrototypeSetpoint l2Prep;
        [SerializeField] private PrototypeSetpoint l2;
        [SerializeField] private PrototypeSetpoint l2Score;
        
        [SerializeField] private PrototypeSetpoint l3Prep;
        [SerializeField] private PrototypeSetpoint l3;
        [SerializeField] private PrototypeSetpoint l3Score;
        
        [SerializeField] private PrototypeSetpoint l4Prep;
        [SerializeField] private PrototypeSetpoint l4;
        [SerializeField] private PrototypeSetpoint l4Score;
        
        [SerializeField] private float armStowAngle = 15;
        
        [Header("Algae Setpoints")]
        [SerializeField] private PrototypeSetpoint groundAlgae;

        [SerializeField] private PrototypeSetpoint lolliAlgae;
        [SerializeField] private PrototypeSetpoint lowAlgae;
        [SerializeField] private PrototypeSetpoint highAlgae;
        [SerializeField] private PrototypeSetpoint bargePrep1;
        [SerializeField] private PrototypeSetpoint bargePrep2;
        [SerializeField] private PrototypeSetpoint bargePlace;
        [SerializeField] private PrototypeSetpoint processAlgae;
        
        [Header("Climb Setpoints")]
        [SerializeField] private PrototypeSetpoint climbPrepClimberOnly;
        [SerializeField] private PrototypeSetpoint climbPrep;
        [SerializeField] private PrototypeSetpoint climbClimb;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState coralStowState;
        [SerializeField] private GamePieceState algaeStowState;
        
        [Header("Algae Stall Audio")]
        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallAudio;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
        [SerializeField] private AudioClip funnelCloseAudio;
        [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        [Header("Clicker Joints")]
        [SerializeField] private GenericAnimationJoint clickerL;
        [SerializeField] private GenericAnimationJoint clickerR;
        [SerializeField] private float ClickerSpeed = 200;
        
        private new void Update() 
        {
            base.Update();
            
            clickerL.SpringLoaded().AllowedDirection(1).RotationSpeed(ClickerSpeed);
            clickerR.SpringLoaded().AllowedDirection(-1).RotationSpeed(ClickerSpeed);
        } 
        
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private float _elevatorTargetHeight;
        private float _armTargetAngle;
        private float _climberTargetAngle;
        private float _funnelTargetAngle;
        private float _eeRollerTargetSpeed;
        private LayerMask coralMask;
        private bool canClack;
        
        private ReefscapeAutoAlign align;

        private bool hasClimbPrepped = false;
        
        private bool placed = false;

        private bool algaePlaced = false;

        private bool aligning = false;

        private bool climberDown = false;

        private bool runOnce = false;
        private bool lowerFunnel = false;

        private bool soring = false;
        private bool algsco = false;
        
        protected override void Start()
        {
            base.Start();
            
            hasClimbPrepped = false;

            placed = false;

            algaePlaced = false;

            align = gameObject.GetComponent<ReefscapeAutoAlign>();
            
            arm.SetPid(armPID);
            funnel.SetPid(funnelPID);
            climber.SetPid(climberPID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _climberTargetAngle = stow.climberAngle;
            _funnelTargetAngle = stow.funnelAngle;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            _coralController.gamePieceStates = new[]
            {
                coralStowState
            };
            _coralController.intakes.Add(coralIntake);

            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
            algaeStallSource.clip = algaeStallAudio;
            algaeStallSource.loop = true;
            algaeStallSource.Stop();
            
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;

        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPID);
            funnel.UpdatePid(funnelPID);
            climber.UpdatePid(climberPID);
        }

        private void FixedUpdate()
        {
            bool hasAlgae = _algaeController.HasPiece();
            bool hasCoral = _coralController.HasPiece();
            
            _algaeController.SetTargetState(algaeStowState);
            _coralController.SetTargetState(coralStowState);

            if (CurrentSetpoint != ReefscapeSetpoints.Climb && CurrentSetpoint != ReefscapeSetpoints.Climbed)
            {
                hasClimbPrepped = false;
            }

            if (!OuttakeAction.IsPressed())
            {
                placed = false;
                algaePlaced = false;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Intake && CurrentSetpoint != ReefscapeSetpoints.Intake)
            {
                _algaeController.RequestIntake(algaeIntake, false);
                _coralController.RequestIntake(coralIntake, false);
            } else if (LastSetpoint == ReefscapeSetpoints.Intake)
            {
                _coralController.RequestIntake(coralIntake, !hasCoral && !hasAlgae);
                UpdateEERollers(hasCoral || hasAlgae ? 0 : eeRollerSpeeds);
            }

            if (AutoAlignLeftAction.IsPressed() || AutoAlignRightAction.IsPressed())
            {
                aligning = true;
            }
            else
            {
                aligning = false;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {

                foreach (var col in EEcolliders)
                {
                    col.enabled = true;
                }
                
            }

            if (CurrentSetpoint != ReefscapeSetpoints.RobotSpecial)
            {
                runOnce = false;
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    rollers[0].SetAngularVelocity(LastSetpoint == ReefscapeSetpoints.Intake ? -8000 : -2000);
                    rollers[1].SetAngularVelocity(LastSetpoint == ReefscapeSetpoints.Intake ? 8000 : 2000);
                    SetSetpoint(hasAlgae ? (lowerFunnel ? algaeStowLow : algaeStow) : (lowerFunnel ? stowLow : stow));
                    if (LastSetpoint == ReefscapeSetpoints.Intake)
                    {
                        _coralController.RequestIntake(coralIntake);
                        UpdateEERollers(hasCoral || hasAlgae ? 0 : eeRollerSpeeds);
                    }
                    else
                    {
                        UpdateEERollers(0);
                    }
                    break;
                case ReefscapeSetpoints.Intake:
                    if (!hasAlgae && !hasCoral)
                    {
                        SetSetpoint(CurrentRobotMode == ReefscapeRobotMode.Coral ? (lowerFunnel ? intakeLow : intake) : groundAlgae);
                    }
                    else
                    {
                        SetSetpoint(hasAlgae ? (lowerFunnel ? algaeStowLow : algaeStow) : (lowerFunnel ? stowLow : stow));
                    }
            
                    if (CurrentRobotMode == ReefscapeRobotMode.Coral)
                    {
                        rollers[0].ChangeAngularVelocity(-8000);
                        rollers[1].ChangeAngularVelocity(8000);
                        UpdateEERollers(hasCoral || hasAlgae ? 0 : eeRollerSpeeds);
                    }
                    else
                    {
                        UpdateEERollers(hasCoral || hasAlgae ? 0 : -eeRollerSpeeds);
                    }
                    
                    _algaeController.RequestIntake(algaeIntake, CurrentRobotMode == ReefscapeRobotMode.Algae && !hasAlgae && !hasCoral && IntakeAction.IsPressed());
                    _coralController.RequestIntake(coralIntake, !hasCoral && !hasAlgae);
                    break;
                case ReefscapeSetpoints.Place:
                    if (hasCoral && ((align.getDistance() < 0.35f && aligning) || !aligning))
                    {
                        StartCoroutine(ScoreCoral(LastSetpoint));

                        placed = true;
                        algaePlaced = true;
                    } 
                    else if (hasCoral && !((align.getDistance() < 0.35f && aligning) || !aligning))
                    {
                        SetState(LastSetpoint);
                    }
                    else if (hasAlgae)
                    {
                        if (LastSetpoint == ReefscapeSetpoints.Barge)
                        {
                            StartCoroutine(ScoreBargeAlgae());
                        }
                        else
                        {
                            PlacePiece();
                        }

                        placed = true;
                        algaePlaced = true;
                    }
                    break;
                case ReefscapeSetpoints.L1:
                    SetSetpoint(l1);
                    break;
                case ReefscapeSetpoints.Stack:
                    SetSetpoint(lolliAlgae);
                    if (IntakeAction.IsPressed()) UpdateEERollers(-eeRollerSpeeds);
                    else UpdateEERollers(0);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                    _coralController.RequestIntake(coralIntake, false);
                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(align.getDistance() < 0.2f && aligning ? l2 : l2Prep);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    SetSetpoint(lowAlgae);
                    if (IntakeAction.IsPressed()) UpdateEERollers(-eeRollerSpeeds);
                    else UpdateEERollers(0);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                    _coralController.RequestIntake(coralIntake, false);
                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(align.getDistance() < 0.2f && aligning ? l3 : l3Prep);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetSetpoint(highAlgae);
                    if (IntakeAction.IsPressed()) UpdateEERollers(-eeRollerSpeeds);
                    else UpdateEERollers(0);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                    _coralController.RequestIntake(coralIntake, false);
                    break;
                case ReefscapeSetpoints.L4:
                    SetSetpoint(align.getDistance() < 0.2f && aligning ? l4 : l4Prep);
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processAlgae);
                    break;
                case ReefscapeSetpoints.Barge:
                    SetSetpoint(bargePrep1);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    if (!runOnce)
                    {
                        lowerFunnel = !lowerFunnel;
                        runOnce = true;
                        SetState(ReefscapeSetpoints.Stow);
                    }
                    break;
                case ReefscapeSetpoints.Climb:
                    StartCoroutine(prepClimberBeforeFunnel());
                    
                    hasClimbPrepped = true;
                    climberDown = true;
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(climbClimb);
                    climberDown = true;
                    break;
            }

            if (lowerFunnel && _funnelTargetAngle == stow.funnelAngle)
            {
                _funnelTargetAngle -= lowerDistance.value;
            } else if (!lowerFunnel && _funnelTargetAngle == (stow.funnelAngle - lowerDistance.value))
            {
                _funnelTargetAngle = stow.funnelAngle;
            }
            
            UpdateSetpoints();
            UpdateAudio();
        }

        private void UpdateEERollers(float speed)
        {
            _eeRollerTargetSpeed = speed;
        }
        
        private void UpdateAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying || algaeStallSource.isPlaying)
                {
                    rollerSource.Stop();
                    algaeStallSource.Stop();
                }

                return;
            }
            
            if (((IntakeAction.IsPressed() && !_algaeController.HasPiece() && !_coralController.HasPiece()) || (OuttakeAction.IsPressed() && soring) || algsco) && !rollerSource.isPlaying)
            {
                rollerSource.Play();
            }
            else if (OuttakeAction.IsPressed() && !soring && !algsco)
            {
                rollerSource.Stop();
            }
            else if (!IntakeAction.IsPressed() && !OuttakeAction.IsPressed() && rollerSource.isPlaying && !algsco)
            {
                rollerSource.Stop();
            }
            else if (IntakeAction.IsPressed() && (_coralController.HasPiece() || _algaeController.HasPiece()))
            {
                rollerSource.Stop();
            }
            
            if (_algaeController.HasPiece() && !algaeStallSource.isPlaying)
            {
                algaeStallSource.Play();
            }
            else if (!_algaeController.HasPiece() && algaeStallSource.isPlaying)
            {
                algaeStallSource.Stop();
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

        public IEnumerator ScoreBargeAlgae()
        {
            if (!algaePlaced)
            {
                algsco = true;
                _armTargetAngle = bargePrep2.armAngle;

                foreach (var col in EEcolliders)
                {
                    col.enabled = false;
                }
                
                yield return new WaitForSeconds(0.15f);

                _armTargetAngle = bargePlace.armAngle;
                SetSetpoint(bargePlace);
                UpdateEERollers(eeRollerSpeeds);
                
                yield return new WaitForSeconds(0.03f);
                
                PlacePiece();
                
                yield return new WaitForSeconds(0.03f);
                
                foreach (var col in EEcolliders)
                {
                    col.enabled = true;
                }
                
                yield return new WaitForSeconds(.35f);
                UpdateEERollers(0);
                algsco = false;
            }
        }

        private IEnumerator ScoreCoral(ReefscapeSetpoints lastSetpoint)
        {
            if (!placed && OuttakeAction.IsPressed())
            {
                switch (lastSetpoint)
                {
                    case ReefscapeSetpoints.L4:
                        _armTargetAngle = l4Score.armAngle;
                        soring = true;

                        yield return new WaitForSeconds(0.023f);

                        PlacePiece();
                        UpdateEERollers(eeRollerSpeeds);

                        yield return new WaitForSeconds(0.2f);

                        _armTargetAngle = 10;
                        UpdateEERollers(0);
                        soring = false;

                        break;
                    case ReefscapeSetpoints.L3:
                        _armTargetAngle = l3Score.armAngle;
                        soring = true;

                        PlacePiece();
                        UpdateEERollers(eeRollerSpeeds);

                        yield return new WaitForSeconds(0.2f);

                        _armTargetAngle = 15;
                        UpdateEERollers(0);
                        soring = false;

                        break;
                    case ReefscapeSetpoints.L2:
                        _armTargetAngle = l2Score.armAngle;
                        soring = true;

                        PlacePiece();
                        UpdateEERollers(eeRollerSpeeds);

                        yield return new WaitForSeconds(0.2f);

                        _armTargetAngle = 15;
                        UpdateEERollers(0);
                        soring = false;

                        break;
                    case ReefscapeSetpoints.L1:
                        
                        rollers[0].SetAngularVelocity(-2350);
                        rollers[1].SetAngularVelocity(1000);
                        soring = true;

                        PlacePiece();
                        UpdateEERollers(eeRollerSpeeds);
                        
                        yield return new WaitForSeconds(0.3f);
                        
                        rollers[0].SetAngularVelocity(-400);
                        rollers[1].SetAngularVelocity(400);

                        _armTargetAngle = l1Place.armAngle;
                        _elevatorTargetHeight = l1Place.elevatorHeight;
                        UpdateEERollers(0);
                        soring = false;

                        break;
                }
            }
        }
        
        private IEnumerator prepClimberBeforeFunnel()
        {
            if (hasClimbPrepped == false)
            {
                _elevatorTargetHeight = climbPrepClimberOnly.elevatorHeight;
                _armTargetAngle = climbPrepClimberOnly.armAngle;
                _climberTargetAngle = climbPrepClimberOnly.climberAngle;

                yield return new WaitForSeconds(0.67f);
                
                _funnelTargetAngle = climbPrep.funnelAngle;
                
                yield return new WaitForSeconds(0.5f);
                
                _climberTargetAngle = climbPrep.climberAngle;
            }
        }

        private void PlacePiece()
        {
            if (_algaeController.HasPiece())
            {
                if (LastSetpoint == ReefscapeSetpoints.Barge)
                {
                    _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3f, 5.5f));
                }
                else
                {
                    _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 0, 1.5f));
                }
            }
            else
            {
                if (LastSetpoint == ReefscapeSetpoints.L4)
                {
                     _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 4), 0.5f, 1f);
                    //_coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 6));
                }
                else if (LastSetpoint == ReefscapeSetpoints.L1)
                {
                    _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 4));
                }
                else
                {
                    _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 6));
                }
            }
        }

        private IEnumerator stowClimb(PrototypeSetpoint setpoint)
        {
            ArmElevatorArm(setpoint);
            
            _climberTargetAngle = climbPrepClimberOnly.climberAngle;
            
            yield return new WaitForSeconds(5f);
            
            _funnelTargetAngle = setpoint.funnelAngle;

            yield return new WaitForSeconds(5f);
            
            _climberTargetAngle = setpoint.climberAngle;
                
        }

        private void SetSetpoint(PrototypeSetpoint setpoint)
        {
            if (CurrentSetpoint == ReefscapeSetpoints.Climbed)
            {
                _climberTargetAngle = climbClimb.climberAngle;
            }
            else if (climberDown && LastSetpoint == ReefscapeSetpoints.Climbed)
            {
                StartCoroutine(stowClimb(setpoint));
                climberDown = false;
            }
            else if (LastSetpoint != ReefscapeSetpoints.Climbed)
            {
                ArmElevatorArm(setpoint);
                _funnelTargetAngle = setpoint.funnelAngle;
                _climberTargetAngle = setpoint.climberAngle;
                
            }
        }

        private void ArmElevatorArm(PrototypeSetpoint setpoint)
        {
            if (Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 5f))
            {
                _armTargetAngle = setpoint.armAngle;
            }
            else
            {
                if (Utils.InRange(arm.GetSingleAxisAngle(JointAxis.X), armStowAngle, 1f))
                {
                    _elevatorTargetHeight = setpoint.elevatorHeight;
                }
                else
                {
                    _armTargetAngle = armStowAngle;
                }
            }
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(15);
            funnel.SetTargetAngle(_funnelTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(-10);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X).noWrap(90).useCustomStartingOffset(100);
            
            foreach (var roller in endEffectorRollers)
            {
                roller.VelocityRoller(_eeRollerTargetSpeed);
            }
        }
    }
}