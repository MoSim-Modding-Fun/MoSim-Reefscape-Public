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
        
        [Header("Setpoints")]
        [SerializeField] private RoboGymSetpoint stowSetpoint, intakeSetpoint;
        [SerializeField] private RoboGymSetpoint l1, l2, l3, l4;
        [SerializeField] private Vector3 coralOuttakeForce, coralL1OuttakeForce;

        [Header("Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;

        [Header("Gamepiece Stow States")]
        [SerializeField] private GamePieceState coralStowState;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        

        private float _elevatorTargetHeight;
        
        protected override void Start()
        {
            base.Start();

            _elevatorTargetHeight = 0;
            
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
                    SetSetpoint(stowSetpoint);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(stowSetpoint);
                    break;
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