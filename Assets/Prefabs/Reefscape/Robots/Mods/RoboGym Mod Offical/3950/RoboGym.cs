using System;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.RoboGym._3950
{
    public class RoboGym : ReefscapeRobotBase
    {
        [Header("Joints")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint climber;
        
        [Header("PIDs")]
        [SerializeField] private PidConstants climberPid;
        
        [Header("Setpoints")]
        [SerializeField] private ClimbPositions climbPositions;
        [SerializeField] private RoboGymSetpoint stowSetpoint, intakeSetpoint;
        [SerializeField] private RoboGymSetpoint l1, l2, l3, l4;

        [Header("Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;

        [Header("Gamepiece Stow States")]
        [SerializeField] private GamePieceState coralStowState;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        
        [Serializable]
        private struct ClimbPositions
        {
            public float climbStowPosition;
            public float climbPrepPosition;
            public float climbClimbPosition;
        }

        private float _elevatorTargetHeight;
        private float _climberTargetAngle;
        
        protected override void Start()
        {
            base.Start();
            
            climber.SetPid(climberPid);

            _elevatorTargetHeight = 0;
            _climberTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());

            _coralController.gamePieceStates = new[]
            {
                coralStowState
            };
            _coralController.intakes.Add(coralIntake);
        }

        private void LateUpdate()
        {
            climber.UpdatePid(climberPid);
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            
            _coralController.SetTargetState(coralStowState);

            if (CurrentRobotMode == ReefscapeRobotMode.Algae)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }
            
            PreventAlgaeSetpoints();
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    SetSetpoint(stowSetpoint);
                    break;
                case ReefscapeSetpoints.Intake:
                    SetSetpoint(intakeSetpoint);
                    _coralController.RequestIntake(coralIntake, !hasCoral);
                    break;
                case ReefscapeSetpoints.Place:
                    PlacePiece();
                    break;
                case ReefscapeSetpoints.L1:
                    SetSetpoint(l1);
                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(l2);
                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(l3);
                    break;
                case ReefscapeSetpoints.L4:
                    SetSetpoint(l4);
                    break;
                case ReefscapeSetpoints.Climb:
                    SetSetpoint(stowSetpoint);
                    SetClimberAngle(climbPositions.climbPrepPosition);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(stowSetpoint);
                    SetClimberAngle(climbPositions.climbClimbPosition);
                    break;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Climb || CurrentSetpoint != ReefscapeSetpoints.Climbed)
            {
                SetClimberAngle(climbPositions.climbStowPosition);
            }
            
            UpdateSetpoints();
        }

        private void PreventAlgaeSetpoints()
        {
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Barge:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Processor:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Stack:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
        }

        private void SetSetpoint(RoboGymSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
        }

        private void SetClimberAngle(float angle)
        {
            _climberTargetAngle = angle;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(0f);
        }

        private void PlacePiece()
        {
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 0));
        }
    }
}