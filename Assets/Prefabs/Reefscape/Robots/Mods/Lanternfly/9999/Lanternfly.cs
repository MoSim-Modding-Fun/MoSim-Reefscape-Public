using System;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Lanternfly._9999
{
    public class Lanternfly: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]        
        [SerializeField] private GenericElevator elevator;
            [SerializeField] private GenericJoint arm, funnelMainLinkage, climber;
            [SerializeField] private GenericRoller[] climbRollers;
            [SerializeField] private BoxCollider climbCollider;
            [SerializeField] private ClimbScorer scorer;
        
        [Header("PIDS")]        
        [SerializeField] private PidConstants armPid;
            [SerializeField] private PidConstants funnelPid, climberPid;

        [Header("Setpoints")] 
        [SerializeField] private LanternflySetpoint stow;
            [SerializeField] private LanternflySetpoint intake, l1, l2, l3, l4;
            [SerializeField] private LanternflySetpoint lowDescore, highDescore;
            [SerializeField] private LanternflySetpoint climbPrep, climbClimb;
        
        [Header("Intake Components")]        
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        
        [Header("Game Piece States")]        
        [SerializeField] private GamePieceState coralStowState;
        
        [Header("Animation Wheels")]
        [SerializeField] private GenericAnimationJoint[] endEffectorWheels;
            [SerializeField] private float endEffectorWheelsSpeeds;
        
        [Header("Robot Audio")]        
        [SerializeField] private AudioSource rollerSource;
            [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]        
        [SerializeField] private AudioSource funnelCloseSource;
            [SerializeField] private AudioClip funnelCloseAudio;
            [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        [Header("Climb Roller Audio")]
        [SerializeField] private AudioSource climbRollerSource;
            [SerializeField] private AudioClip climbRollerClip;
    
        [Header("Climb Click Audio")]
        [SerializeField] private AudioSource climbClickSource;
            [SerializeField] private AudioClip climbClickClip;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private float _elevatorTargetHeight, _armTargetAngle, _funnelMainLinkageTargetAngle, _climberTargetAngle;

        private LayerMask coralMask;
        private bool canClack;
        
        private ReefscapeAutoAlign align;
        
        private Vector3 _blueReef;
        private Vector3 _redReef;
        
        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPid);
            funnelMainLinkage.SetPid(funnelPid);
            climber.SetPid(climberPid);

            _elevatorTargetHeight = 0;
            _armTargetAngle = 0;
            _funnelMainLinkageTargetAngle = 0;
            _climberTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));

            _coralController.gamePieceStates = new[]
            {
                coralStowState
            };
            _coralController.intakes.Add(coralIntake);
            
            align = gameObject.GetComponent<ReefscapeAutoAlign>();
            
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);
            canClack = true;
            
            _blueReef = GameObject.Find("BlueReef").transform.position;
            _redReef = GameObject.Find("RedReef").transform.position;
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPid);
            climber.UpdatePid(climberPid);
            funnelMainLinkage.UpdatePid(funnelPid);
        }

        private void FixedUpdate()
        {
            if (CurrentRobotMode == ReefscapeRobotMode.Algae)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }
            
            bool hasCoral = _coralController.atTarget;
            
            _coralController.RequestIntake(coralIntake, AtSetpoint(intake) && !hasCoral);

            climbCollider.enabled = scorer.AutoClimbTriggered;
            
            if (hasCoral)
            {
                switch (CurrentSetpoint)
                {
                    case ReefscapeSetpoints.L4: 
                        SetSetpoint(l4); 
                        break;
                    
                    case ReefscapeSetpoints.L3: 
                        SetSetpoint(l3); 
                        break;
                    
                    case ReefscapeSetpoints.L2: 
                        SetSetpoint(l2); 
                        break;
                    
                    case ReefscapeSetpoints.L1: 
                        SetSetpoint(l1); 
                        break;
                }
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: 
                    SetSetpoint(stow);
                    SetEndEffectorWheels(_coralController.HasPiece() ? 0 : endEffectorWheelsSpeeds * 1.5f);
                    break;
                
                case ReefscapeSetpoints.Intake:
                    if (_coralController.HasPiece()) { SetState(ReefscapeSetpoints.Stow); return; }
                    
                    SetSetpoint(intake);
                    break;
                
                case ReefscapeSetpoints.LowAlgae: 
                    SetSetpoint(lowDescore); 
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.HighAlgae: 
                    SetSetpoint(highDescore); 
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.Climb: 
                    SetSetpoint(climbPrep);
                    if (scorer.AutoClimbTriggered)
                    {
                        SetState(ReefscapeSetpoints.Climbed);
                        climbClickSource.Play();
                    }
                    break;
                
                case ReefscapeSetpoints.Climbed: 
                    SetSetpoint(climbClimb);
                    break;
                
                case ReefscapeSetpoints.Place: 
                    PlacePiece();
                    if (OuttakeAction.IsPressed()) SetEndEffectorWheels(endEffectorWheelsSpeeds); else SetEndEffectorWheels(0);
                    break;
                
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Processor:
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
                case ReefscapeSetpoints.Stack:
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
                case ReefscapeSetpoints.Barge:
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
            }
            
            UpdateSetpoints();
            UpdateRollers();
            //UpdateAudio();
        }

        #region Actuators & Setpoints
        
        private void SetSetpoint(LanternflySetpoint setpoint)
        {
            _funnelMainLinkageTargetAngle = setpoint.funnelAngle;
            _climberTargetAngle = setpoint.climbAngle;

            bool goingToStowOrIntake = CurrentSetpoint == ReefscapeSetpoints.Stow || 
                                       CurrentSetpoint == ReefscapeSetpoints.Intake;
            bool comingFromStowOrIntake = LastSetpoint == ReefscapeSetpoints.Intake || 
                                          LastSetpoint == ReefscapeSetpoints.Stow;

            if (goingToStowOrIntake)
            {
                // Coming down: elevator first, arm comes in only once elevator is near setpoint
                _elevatorTargetHeight = setpoint.elevatorHeight;
                if (ElevatorAtSetpoint(setpoint))
                {
                    _armTargetAngle = setpoint.armAngle;
                }
            }
            else if (comingFromStowOrIntake)
            {
                // Going up: arm out first, elevator goes up once arm is out
                _armTargetAngle = setpoint.armAngle;
                if (ArmAtSetpoint(setpoint))
                {
                    _elevatorTargetHeight = setpoint.elevatorHeight;
                }
            }
            else
            {
                // Mid-scoring transition: move both freely
                _armTargetAngle = setpoint.armAngle;
                _elevatorTargetHeight = setpoint.elevatorHeight;
            }
        }
        
        private void SetEndEffectorWheels(float speed)
        {
            foreach (var roller in endEffectorWheels)
            {
                roller.VelocityRoller(speed);
            }
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle)
                .withAxis(JointAxis.Z);
            arm.SetTargetAngle(_armTargetAngle)
                .withAxis(JointAxis.X)
                .noWrap(270);
            funnelMainLinkage.SetTargetAngle(_funnelMainLinkageTargetAngle)
                .withAxis(JointAxis.X);
        }

        private void UpdateRollers()
        {
            if (CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                foreach (var roller in climbRollers)
                {
                    roller.ChangeAngularVelocity(1500f);
                }
            }
        }
        
        #endregion
        

        #region Logic Helpers

        private bool ArmAtSetpoint(LanternflySetpoint setpoint = null)
        {
            return Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), setpoint == null ? _armTargetAngle : setpoint.armAngle, 2f);
        }

        private bool ElevatorAtSetpoint(LanternflySetpoint setpoint = null)
        {
            return Utils.InRange(elevator.GetElevatorHeight(), setpoint == null ? _elevatorTargetHeight : setpoint.elevatorHeight, 2f);
        }

        private bool AtSetpoint(LanternflySetpoint setpoint = null)
        {

            return ElevatorAtSetpoint(setpoint) && ArmAtSetpoint(setpoint);
        }
        
        /*
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

            if (CurrentSetpoint == ReefscapeSetpoints.Climbed || CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                rollerSource.Stop();
                return;
            }

            if ((((AtSetpoint(intake) && !_coralController.HasPiece()) ||
                 (OuttakeAction.IsPressed() && !AtSetpoint(stow))) || (_coralController.HasPiece() && !CoralAtState(coralStowState))) &&
                !rollerSource.isPlaying)
            {
                rollerSource.Play();
            }
            else if ((!AtSetpoint(intake) && (!_coralController.HasPiece() || CoralAtState(coralStowState))) && !OuttakeAction.IsPressed() && rollerSource.isPlaying )
            {
                rollerSource.Stop();
            }
            else if (AtSetpoint(intake) && (CoralAtState(coralStowState)))
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
*/
        private void PlacePiece()
        {
            if (LastSetpoint == ReefscapeSetpoints.L4)
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 4), 0.35f, 0.6f);
                return;
            }
            else if (LastSetpoint == ReefscapeSetpoints.L1)
            {
                
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 1, 1), 0.2f, .75f);
                return;
            }
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 3));
        }
        
        #endregion
    }
}