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
        [SerializeField] private GenericJoint hopper, climber;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants hopperPid;
        [SerializeField] private PidConstants climberPid;
        
        [Header("Setpoints")]
        [SerializeField] private RoboGymSetpoint stowSetpoint, intakeSetpoint;
        [SerializeField] private RoboGymSetpoint l1, l2, l3, l4;
        [SerializeField] private Vector3 coralOuttakeForce, coralL1OuttakeForce;
        [SerializeField] private float climberStow, climberOut, climberClimb;
        [SerializeField] private float hopperAngle, hopperClimbAngle;

        [Header("Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;

        [Header("Gamepiece Stow States")]
        [SerializeField] private GamePieceState coralStowState;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private float _climberTargetAngle;
        private float _hopperTargetAngle;

        private float _elevatorTargetHeight;
        
        protected override void Start()
        {
            base.Start();

            _elevatorTargetHeight = 0;
            _climberTargetAngle = climberStow;
            _hopperTargetAngle = hopperAngle;
            
            climber.SetPid(climberPid);
            hopper.SetPid(hopperPid);
            
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
            hopper.UpdatePid(hopperPid);
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
                    _climberTargetAngle = climberOut;
                    _hopperTargetAngle = hopperClimbAngle;
                    SetSetpoint(stowSetpoint);
                    break;
                case ReefscapeSetpoints.Climbed:
                    _climberTargetAngle = climberClimb;
                    _hopperTargetAngle = hopperClimbAngle;
                    SetSetpoint(stowSetpoint);
                    break;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Climb && CurrentSetpoint != ReefscapeSetpoints.Climbed)
            {
                _climberTargetAngle = climberStow;
                _hopperTargetAngle = hopperAngle;
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
                case ReefscapeSetpoints.L1:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
        }

        private void SetSetpoint(RoboGymSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X).noWrap(290);
            hopper.SetTargetAngle(_hopperTargetAngle).withAxis(JointAxis.X);
        }

        private void PlacePiece()
        {
            if (LastSetpoint == ReefscapeSetpoints.L1 || CurrentSetpoint == ReefscapeSetpoints.L1)
            {
                _coralController.ReleaseGamePieceWithForce(coralL1OuttakeForce);
                return;
            }

            //_coralController.ReleaseGamePieceWithForce(coralOuttakeForce);
            _coralController.ReleaseGamePieceWithContinuedForce(coralOuttakeForce, .4f, .5f);
        }
    }
}