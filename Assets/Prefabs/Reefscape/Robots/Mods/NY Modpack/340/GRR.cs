using System.Collections;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.GRR._340
{
    public class GRR : ReefscapeRobotBase
    {
        [Header("Robot Components")]
        [SerializeField] private GenericJoint wrist;
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint climber;

        [Header("Motion Controllers")]
        [SerializeField] private GRRAutoAlign autoAlign;
        [SerializeField] private PidConstants wristPID;
        [SerializeField] private PidConstants climberPID;

        [Header("Setpoints")]
        [SerializeField] private float safeDistance = 1.6f;
        [SerializeField] private float climberNoWrapAngle = -100f;
        [SerializeField] private GRRSetpoint stow;
        [SerializeField] private GRRSetpoint coralIntake;
        [SerializeField] private GRRSetpoint coralStow;
        [SerializeField] private GRRSetpoint l1Left;
        [SerializeField] private GRRSetpoint l1Right;
        [SerializeField] private GRRSetpoint l2;
        [SerializeField] private GRRSetpoint l2Place;
        [SerializeField] private GRRSetpoint l3;
        [SerializeField] private GRRSetpoint l3Place;
        [SerializeField] private GRRSetpoint l4;
        [SerializeField] private GRRSetpoint l4Place;
        [SerializeField] private GRRSetpoint lowAlgae;
        [SerializeField] private GRRSetpoint highAlgae;
        [SerializeField] private GRRSetpoint climb;
        [SerializeField] private GRRSetpoint climbed;

        [Header("Game Piece Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntakeComponent;
        [SerializeField] private GamePieceState coralIntakeState;
        [SerializeField] private GamePieceState coralStowState;

        [Header("Audio")]
        [SerializeField] private AudioSource gooseAudioSource;
        [SerializeField] private AudioClip gooseAudioClip;
        [SerializeField] private AudioSource intakeAudioSource;
        [SerializeField] private AudioClip intakeAudioClip;

        [Header("Goose Wheel Animation")]
        [SerializeField] private GenericAnimationJoint[] gooseAnimationWheels;
        [SerializeField] private float gooseAnimationWheelSpeed = 900f;

        [Header("Intake Wheel Animation")] 
        [SerializeField] private GenericAnimationJoint[] intakeAnimationWheels;
        [SerializeField] private float intakeAnimationWheelSpeed = 900f;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private bool _alreadyPlaced = false;
        private float _placingUntil = 0f;

        private float _gooseWheels;
        private float _intakeWheels;
        private float _wristAngle;
        private float _elevatorDistance;
        private float _climberAngle;
        private GRRSetpoint _currentSetpoint;

        protected override void Start()
        {
            base.Start();

            wrist.SetPid(wristPID);
            climber.SetPid(climberPID);

            _currentSetpoint = stow;
            _wristAngle = stow.wristTarget;
            _elevatorDistance = stow.elevatorDistance;
            _climberAngle = stow.climberTarget;

            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _coralController.gamePieceStates = new[] { coralIntakeState, coralStowState };
            _coralController.intakes.Add(coralIntakeComponent);
            
            if (gooseAudioSource != null && gooseAudioClip != null)
            {
                gooseAudioSource.clip = gooseAudioClip;
                intakeAudioSource.volume = 0.2f;
                gooseAudioSource.loop = true;
                gooseAudioSource.Stop();
            }

            if (intakeAudioSource != null && intakeAudioClip != null)
            {
                intakeAudioSource.clip = intakeAudioClip;
                intakeAudioSource.volume = 0.2f;
                intakeAudioSource.loop = true;
                intakeAudioSource.Stop();
            }
        }

        private void LateUpdate()
        {
            wrist.UpdatePid(wristPID);
            climber.UpdatePid(climberPID);
        }

        private void FixedUpdate()
        {
            SetRobotMode(ReefscapeRobotMode.Coral); // hehe
            SetWheelSpeeds(0, 0);

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                intakeAudioSource?.Stop();
                gooseAudioSource?.Stop();

                climber.SetTargetAngle(_climberAngle).withAxis(JointAxis.Z).flipDirection().noWrap(climberNoWrapAngle);

                return;
            }

            bool hasCoral = _coralController.HasPiece();
            bool coralSeated = _coralController.atTarget && _coralController.GetCurrentState().Equals(coralStowState);
            bool autoPlace = autoAlign.InPosition();
            bool safe = autoAlign.ReefDistance() >= safeDistance;

            if (autoPlace) SetState(ReefscapeSetpoints.Place);
            if (CurrentSetpoint != ReefscapeSetpoints.Place) _alreadyPlaced = false;

            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stack:
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Stow:
                case ReefscapeSetpoints.Intake:
                    if (safe)
                    {
                        if (!hasCoral)
                        {
                            _coralController.SetTargetState(coralIntakeState);
                            _coralController.RequestIntake(coralIntakeComponent, IntakeAction.IsPressed());
                            SetSetpoint(IntakeAction.IsPressed() ? coralIntake : stow);
                        }
                        else
                        {
                            _coralController.SetTargetState(coralStowState);
                            SetSetpoint(!coralSeated ? coralIntake : coralStow);
                        }
                
                        if (!coralSeated && IntakeAction.IsPressed())
                        {
                            SetWheelSpeeds(gooseAnimationWheelSpeed, intakeAnimationWheelSpeed);
                        }
                    }
                    
                    break;
                case ReefscapeSetpoints.Place:
                    if (!_alreadyPlaced && (OuttakeAction.triggered || autoPlace))
                    {
                        StartCoroutine(PlacePiece());
                        _alreadyPlaced = true;
                    }

                    if (Time.time < _placingUntil) SetWheelSpeeds(-gooseAnimationWheelSpeed, 0);
                    if (_alreadyPlaced && safe) SetSetpoint(stow);
                    break;
                case ReefscapeSetpoints.L1:
                    if (coralSeated) SetSetpoint(autoAlign.Left() ? l1Left : l1Right);
                    break;
                case ReefscapeSetpoints.L2:
                    if (coralSeated) SetSetpoint(l2);
                    else if (!hasCoral) SetState(ReefscapeSetpoints.LowAlgae);
                    break;
                case ReefscapeSetpoints.L3:
                    if (coralSeated) SetSetpoint(l3);
                    else if (!hasCoral) SetState(ReefscapeSetpoints.HighAlgae);
                    break;
                case ReefscapeSetpoints.L4:
                    if (coralSeated) SetSetpoint(l4);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    if (!hasCoral)
                    {
                        SetSetpoint(lowAlgae);
                        SetWheelSpeeds(-gooseAnimationWheelSpeed, 0);
                    }
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    if (!hasCoral)
                    {
                        SetSetpoint(highAlgae);
                        SetWheelSpeeds(-gooseAnimationWheelSpeed, 0);
                    }
                    break;
                case ReefscapeSetpoints.Climb:
                    SetSetpoint(climb);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(climbed);
                    break;
                default:
                    throw new System.ArgumentOutOfRangeException();
            }

            UpdateSetpoints();
        }

        private void SetWheelSpeeds(float goose, float intake)
        {
            _gooseWheels = goose;
            _intakeWheels = intake;
        }

        private void SetSetpoint(GRRSetpoint setpoint)
        {
            _currentSetpoint = setpoint;
            _wristAngle = setpoint.wristTarget;
            _elevatorDistance = setpoint.elevatorDistance;
            _climberAngle = setpoint.climberTarget;
        }

        private IEnumerator PlacePiece()
        {
            if (_alreadyPlaced) yield break;

            if (_coralController.HasPiece() && _coralController.atTarget && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                float time;
                float maxSpeed;
                Vector3 force;

                if (_currentSetpoint == l4)
                {
                    time = 0.35f;
                    maxSpeed = 5f;
                    force = new Vector3(0, 0.4f, 0.6f);

                    SetSetpoint(l4Place);
                }
                else if (_currentSetpoint == l3 || _currentSetpoint == l2)
                {
                    time = 0.3f;
                    maxSpeed = 0.8f;
                    force = new Vector3(0, 0, 4f);

                    SetSetpoint(_currentSetpoint == l3 ? l3Place : l2Place);
                }
                else if (_currentSetpoint == l1Left || _currentSetpoint == l1Right)
                {
                    time = 0.4f;
                    maxSpeed = 0.5f;
                    force = new Vector3(0, 0f, 3f);
                }
                else
                {
                    time = 0.3f;
                    maxSpeed = 1.2f;
                    force = new Vector3(0, 0f, -1f);
                }

                _placingUntil = Time.time + time;
                _coralController.ReleaseGamePieceWithContinuedForce(force, time, maxSpeed);
            }
            
            yield break;
        }

        private void UpdateSetpoints()
        {
            wrist.SetTargetAngle(_wristAngle).withAxis(JointAxis.X).flipDirection();
            elevator.SetTarget(_elevatorDistance);
            climber.SetTargetAngle(_climberAngle).withAxis(JointAxis.Z).flipDirection().noWrap(climberNoWrapAngle);

            for (int i = 0; i < gooseAnimationWheels.Length; i++)
            {
                gooseAnimationWheels[i].VelocityRoller(_gooseWheels * (i < 3 ? -1 : 1));
            }

            if (Mathf.Abs(_gooseWheels) > 1e-6)
            {
                if (gooseAudioSource?.isPlaying != true) gooseAudioSource?.Play();
            }
            else
            {
                gooseAudioSource?.Stop();
            }

            for (int i = 0; i < intakeAnimationWheels.Length; i++)
            {
                intakeAnimationWheels[i].VelocityRoller(_intakeWheels * (i < 2 ? 1 : -1));
            }

            if (Mathf.Abs(_intakeWheels) > 1e-6)
            {
                if (intakeAudioSource?.isPlaying != true) intakeAudioSource?.Play();
            }
            else
            {
                intakeAudioSource?.Stop();
            }
        }
    }
}

