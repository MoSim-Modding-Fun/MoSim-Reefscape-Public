using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using Prefabs.Reefscape.Robots.Mods.Wildcats._9483;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Wildcats._9483
{
    public class WildcatsA: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]        
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint climber, climberJointLeft, climberJointRight, algaeDescore;
        
        [Header("PIDS")]        
        [SerializeField] private PidConstants climberPID, climberJointLeftPID, climberJointRightPID, algaeDescorePID;

        [Header("Setpoints")]        
        [SerializeField] private WildcatsSetpoint stow, intake, l1, l2, l3, l4;
        [SerializeField] private WildcatsSetpoint lowDescore, highDescore;
        
        [Header("Climb Setpoints")]        
        [SerializeField] private WildcatsClimbSetpoint climbStow, prep, climb;
        
        [Header("Intake Components")]        
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        
        [Header("Game Piece States")]        
        [SerializeField] private GamePieceState funnelCoralState, coralTransferState1, coralStowState;
        
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
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private float _elevatorTargetHeight, _climberTargetAngle, _climberLeftPincerTarget, _climberRightPincerTarget, _algaeDescoreTargetAngle;

        private LayerMask coralMask;
        private bool canClack;
        
        private ReefscapeAutoAlign align;
        
        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            climber.SetPid(climberPID);
            climberJointLeft.SetPid(climberJointLeftPID);
            climberJointRight.SetPid(climberJointRightPID);
            algaeDescore.SetPid(algaeDescorePID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _climberTargetAngle = climbStow.elevatorAngle;
            _climberLeftPincerTarget = climbStow.leftPincerAngle;
            _climberRightPincerTarget = climbStow.rightPincerAngle;
            _algaeDescoreTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));

            _coralController.gamePieceStates = new[]
            {
                coralTransferState1,
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
        }

        private void LateUpdate()
        {
            climber.UpdatePid(climberPID);
            climberJointLeft.UpdatePid(climberJointLeftPID);
            climberJointRight.UpdatePid(climberJointRightPID);
            algaeDescore.UpdatePid(algaeDescorePID);
        }

        private void FixedUpdate()
        {
            AutoAlignLogic();
            
            bool hasCoral = _coralController.atTarget;
            
            _coralController.RequestIntake(coralIntake, AtSetpoint(intake) && IntakeAction.IsPressed() && !hasCoral);
            
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
            
            AnimateCoralHandoff();
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: 
                    SetSetpoint(stow); 
                    SetAlgaeDescoreAngle(0); 
                    break;
                
                case ReefscapeSetpoints.Intake:
                    if (!hasCoral) _coralController.SetTargetState(funnelCoralState);
                    SetSetpoint(intake);
                    break;
                
                case ReefscapeSetpoints.LowAlgae: 
                    SetSetpoint(lowDescore); 
                    SetAlgaeDescoreAngle(130); 
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.HighAlgae: 
                    SetSetpoint(highDescore);
                    SetAlgaeDescoreAngle(110); 
                    if(IntakeAction.IsPressed()) SetState(ReefscapeSetpoints.Intake); 
                    break;
                
                case ReefscapeSetpoints.Climb: 
                    SetSetpoint(intake); 
                    SetClimberAngle(AtSetpoint(intake) ? prep : climbStow); 
                    break;
                
                case ReefscapeSetpoints.Climbed: 
                    SetSetpoint(intake); 
                    SetClimberAngle(climb); 
                    break;
                
                case ReefscapeSetpoints.Place: 
                    PlacePiece();
                    if (OuttakeAction.IsPressed()) SetEndEffectorWheels(endEffectorWheelsSpeeds); else SetEndEffectorWheels(0);
                    break;
                
                case ReefscapeSetpoints.RobotSpecial: 
                    SetAlgaeDescoreAngle(0); 
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
                case ReefscapeSetpoints.Processor: 
                    SetAlgaeDescoreAngle(0); 
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
                case ReefscapeSetpoints.Stack: 
                    SetAlgaeDescoreAngle(0); 
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
                case ReefscapeSetpoints.Barge: 
                    SetAlgaeDescoreAngle(0); 
                    SetState(ReefscapeSetpoints.Stow); 
                    break;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Climb && CurrentSetpoint != ReefscapeSetpoints.Climbed &&
                LastSetpoint != ReefscapeSetpoints.Climb && LastSetpoint != ReefscapeSetpoints.Climbed)
            {
                SetClimberAngle(climbStow);
            }
            
            UpdateSetpoints();
            UpdateAudio();
        }

        #region Actuators & Setpoints
        
        private void SetSetpoint(WildcatsSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
        }

        private void SetClimberAngle(WildcatsClimbSetpoint setpoint)
        {
            _climberTargetAngle = setpoint.elevatorAngle;
            _climberLeftPincerTarget = setpoint.leftPincerAngle;
            _climberRightPincerTarget = setpoint.rightPincerAngle;
        }

        private void SetAlgaeDescoreAngle(float algaeDescoreAngle)
        {
            _algaeDescoreTargetAngle = algaeDescoreAngle;
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
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X).noWrap(-90);
            climberJointLeft.SetTargetAngle(_climberLeftPincerTarget).withAxis(JointAxis.Y).noWrap(90);
            climberJointRight.SetTargetAngle(_climberRightPincerTarget).withAxis(JointAxis.Y).noWrap(-90);
            algaeDescore.SetTargetAngle(_algaeDescoreTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(-30);

        }
        
        #endregion
        

        #region Logic Helpers

        private bool CoralAtState(GamePieceState state)
        {
            return _coralController.atTarget && _coralController.currentStateNum == state.stateNum;
        }

        private bool AtSetpoint(WildcatsSetpoint targetSetpoint)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetSetpoint.elevatorHeight, 2f);

            return elevatorAtSetpoint;
        }

        private bool AtSetpoint()
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), _elevatorTargetHeight, 2f);

            return elevatorAtSetpoint;
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

        private bool IsCoralSetpoint()
        {
            return CurrentSetpoint == ReefscapeSetpoints.L4 ||
                   CurrentSetpoint == ReefscapeSetpoints.L3 ||
                   CurrentSetpoint == ReefscapeSetpoints.L2 ||
                   CurrentSetpoint == ReefscapeSetpoints.L1 ||
                   LastSetpoint == ReefscapeSetpoints.L4 ||
                   LastSetpoint == ReefscapeSetpoints.L3 ||
                   LastSetpoint == ReefscapeSetpoints.L2 ||
                   LastSetpoint == ReefscapeSetpoints.L1;
        }

        private WildcatsSetpoint GetCurrentCoralSetpointSetpoint()
        {
            switch (LastSetpoint)
            {
                case ReefscapeSetpoints.L4: return l4;
                case ReefscapeSetpoints.L3: return l3;
                case ReefscapeSetpoints.L2: return l2;
                case ReefscapeSetpoints.L1: return l1;
            }

            return l4;
        }

        private void PlacePiece()
        {
            if (!IsCoralSetpoint() || !AtSetpoint(GetCurrentCoralSetpointSetpoint()) || !CoralAtState(coralStowState)) return;

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

        private void AnimateCoralHandoff()
        {
            if (!AtSetpoint(intake)) return;

            if (CoralAtState(funnelCoralState))
            {
                _coralController.SetTargetState(coralTransferState1);
            }
            else if (CoralAtState(coralTransferState1))
            {
                _coralController.SetTargetState(coralStowState);
            }
            else
            {
                _coralController.SetTargetState(funnelCoralState);
            }
            
            

            if (AtSetpoint(intake) && !CoralAtState(coralStowState))
            {
                if (_coralController.currentStateNum != coralStowState.stateNum)
                {
                    SetEndEffectorWheels(endEffectorWheelsSpeeds);
                }
                else
                {
                    SetEndEffectorWheels(-endEffectorWheelsSpeeds);
                }
            }
        }

        private void AutoAlignLogic()
        {
            if (CurrentSetpoint == ReefscapeSetpoints.L4 ||
                LastSetpoint == ReefscapeSetpoints.L4)
            {
                align.offset = new Vector3(0.0f, 0, 11f);
            }
            else
            {
                align.offset = new Vector3(0.0f, 0, 7f);
            }
        }
        
        #endregion
    }
}