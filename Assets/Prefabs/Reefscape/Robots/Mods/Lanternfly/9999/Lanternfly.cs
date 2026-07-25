using System;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
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
            [SerializeField] private GenericJoint arm, funnelMainLinkage;
            [SerializeField] private GenericRoller algaeRoller;
            [SerializeField] private float algaeRollerRpm;
            [SerializeField] private LanternClimb climber;
            [SerializeField] private ClimbScorer scorer;
            [SerializeField] private BoxCollider coralBlocker;
        
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
        [SerializeField] private GenericAnimationJoint[] climberWheels;
            [SerializeField] private float climberWheelSpeeds;
        
        [Header("Robot Audio")]        
        [SerializeField] private AudioSource rollerSource;
            [SerializeField] private AudioClip rollerAudio;
        
        [Header("Funnel Close Audio")]        
        [SerializeField] private AudioSource funnelCloseSource;
            [SerializeField] private AudioClip funnelCloseAudio;
            [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
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
            
            rollerSource.clip = rollerAudio;
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
            funnelMainLinkage.UpdatePid(funnelPid);
        }

        private void FixedUpdate()
        {
            
            // foreach (var roller in endEffectorWheels)
            // {
            //     roller.gameObject.transform.localScale = _coralController.atTarget && _coralController.HasPiece() ? new Vector3(0.8f, 0.8f, 0.8f) : new Vector3(1f, 1f, 1f);
            // }
            
            coralBlocker.gameObject.SetActive(!_coralController.atTarget);
            
            if (CurrentRobotMode == ReefscapeRobotMode.Algae)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }

            if (ArmAtSetpoint(intake))
            {
                CurrentCoralStationMode.DropDistance = 2f;
            }
            else
            {
                CurrentCoralStationMode.DropDistance = 0f;
            }
            
            bool hasCoral = _coralController.atTarget;
            
            _coralController.RequestIntake(coralIntake, AtSetpoint(intake) && !hasCoral && IntakeAction.IsPressed());
            
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
                    if (climber != null) climber.NotClimbing();
                    break;
                
                case ReefscapeSetpoints.Intake:
                    if (_coralController.atTarget) { SetSetpoint(stow); break; }
                    
                    SetSetpoint(intake);
                    break;
                
                case ReefscapeSetpoints.LowAlgae: 
                    SetRobotMode(ReefscapeRobotMode.Algae);
                    SetSetpoint(lowDescore); 
                    algaeRoller.SetAngularVelocity(algaeRollerRpm);
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.HighAlgae: 
                    SetRobotMode(ReefscapeRobotMode.Algae);
                    SetSetpoint(highDescore); 
                    algaeRoller.SetAngularVelocity(algaeRollerRpm);
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.Climb: 
                    SetSetpoint(climbPrep);
                    climber.Climb();
                    if (climber != null) climber.Climb(); 
                    break;
                
                case ReefscapeSetpoints.Climbed: 
                    SetSetpoint(climbClimb);
                    climber.NotClimbing();
                    if (climber != null) climber.RetractArm();
                    break;
                
                case ReefscapeSetpoints.Place: 
                    PlacePiece();
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
            UpdateAudio();
            AnimateWheels();
            
            // Climber and Drive modifiers remain the same...
            if (scorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climb && climber.WingsOpen())
            {
                climber.PlayClick();
                SetState(ReefscapeSetpoints.Climbed);
            }
            else if (!scorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climbed)
                SetState(ReefscapeSetpoints.Climb);
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

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            arm.SetTargetAngle(_armTargetAngle)
                .withAxis(JointAxis.X)
                .noWrap(270);
            funnelMainLinkage.SetTargetAngle(_funnelMainLinkageTargetAngle)
                .withAxis(JointAxis.X);
        }
        
        #endregion
        

        #region Logic Helpers

        private void AnimateWheels()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
                }
                
                return;
            }
            
            if (CurrentSetpoint == ReefscapeSetpoints.Intake)
            {
                RunRollers(endEffectorWheels, endEffectorWheelsSpeeds);
                if (!rollerSource.isPlaying && !_coralController.atTarget)
                {
                    rollerSource.Play();
                }

                if (_coralController.atTarget)
                {
                    rollerSource.Stop();
                }
            } 
            else if (OuttakeAction.IsPressed() && CurrentSetpoint == ReefscapeSetpoints.Place)
            {
                RunRollers(endEffectorWheels, -endEffectorWheelsSpeeds);
                if (!rollerSource.isPlaying)
                {
                    rollerSource.Play();
                }
            }
            else if (CurrentSetpoint == ReefscapeSetpoints.LowAlgae || CurrentSetpoint == ReefscapeSetpoints.HighAlgae)
            {
                RunRollers(endEffectorWheels, endEffectorWheelsSpeeds);
                if (!rollerSource.isPlaying)
                {
                    rollerSource.Play();
                }
            }
            else
            {
                RunRollers(endEffectorWheels, 0f);
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
                }
            }

            if (CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                RunRollers(climberWheels, climberWheelSpeeds);
            }
            else
            {
                RunRollers(climberWheels, 0f);
            }
        }

        private void RunRollers(GenericAnimationJoint[] rollerGroup, float rollerSpeed)
        {
            foreach (var roller in rollerGroup)
            {
                roller.VelocityRoller(rollerSpeed);
            }
        }

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
        
        private void UpdateAudio()
        {
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
            if (LastSetpoint == ReefscapeSetpoints.L4)
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 4), 0.4f, 0.6f);
                return;
            }
            else if (LastSetpoint == ReefscapeSetpoints.L1)
            {
                _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 3.5f));
                //_coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 2), .3f, .75f);
                return;
            }
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 3));
        }
        
        #endregion
    }
}