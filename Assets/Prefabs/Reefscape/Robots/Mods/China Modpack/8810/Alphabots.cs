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

namespace Prefabs.Reefscape.Robots.Mods.China_Modpack._8810
{
    public class Alphabots: ReefscapeRobotBase
    {
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
            [SerializeField] private GenericJoint funnelJoint, armJoint, climberJoint;
            [SerializeField] private AlphabotsClimb climb;
            [SerializeField] private ClimbScorer climbScorer;
            
        [Header("PIDs")]
        [SerializeField] private PidConstants funnelPid;
            [SerializeField] private PidConstants armPid, climberPid;
            
        [Header("Funnel Values")]
        [SerializeField] private float funnelStowAngle;
            [SerializeField] private float funnelClimbAngle;
            
        [Header("Climber Values")]
        [SerializeField] private float climberStowAngle = 0f;
            [SerializeField] private float climberDeployAngle, climbClimbedAngle;
            
        [Header("Setpoints")]
        [SerializeField] private AlphabotsSetpoint stow;
            [SerializeField] private AlphabotsSetpoint coralPickup;
            [SerializeField] private AlphabotsSetpoint l1, l2, l3, l4;
            [SerializeField] private AlphabotsSetpoint l2place, l3place, l4place;
            [SerializeField] private AlphabotsSetpoint algaeStow, groundAlgae, lowAlgae, highAlgae, lollipop, bargeFront, bargeBack, processor;
        
        [Header("Outtake Forces")]
        [SerializeField] private AlphabotsVector3 algaeOuttakeForce;
            [SerializeField] private AlphabotsVector3 l1OuttakeForce, l2OuttakeForce, l3OuttakeForce, l4OuttakeForce;
            
        [Header("Intakes and States")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
            [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
        
        [Header("Center of Mass")]
        [SerializeField] private bool addCenterOfMassX;
            [SerializeField] private bool addCenterOfMassZ;
            [SerializeField] private float climbedCenterOfMassX;
            [SerializeField] private float climbedCenterOfMassZ;
            private Rigidbody _mainRb;
            private Vector3 _originalCenterOfMass;
            private bool _isCgShifted;

        
        [SerializeField] private GamePieceState coralHandoffState, coralArmState, algaeStowState;
        
        [Header("Rollers")]
        [SerializeField] private GenericAnimationJoint[] armRollers;
            [SerializeField] private float rollerSpeed;
        
        [Header("Funnel Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
            [SerializeField] private AudioClip funnelCloseAudio;
            [SerializeField] private BoxCollider coralTrigger;
            private OverlapBoxBounds soundDetector;
        
        [Header("Roller Audio")]
        [SerializeField] private AudioSource rollerSource;
            [SerializeField] private AudioClip rollerClip;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController, _algaeController;
            
        private float _elevatorTargetHeight = 0f;
        private float _armAngle = 0f;
        private float _funnelAngle = 0f;
        private float _climberAngle = 0f;

        private LayerMask coralMask;
        private bool canClack;
        
        protected override void Start()
        {
            base.Start();
            
            funnelJoint.SetPid(funnelPid);
            armJoint.SetPid(armPid);
            climberJoint.SetPid(climberPid);
            
            _mainRb = gameObject.GetComponent<Rigidbody>();
            _isCgShifted = false;
            if (_mainRb != null)
            {
                _originalCenterOfMass = _mainRb.centerOfMass;
            }
            else
            {
                Debug.LogWarning("ts isnt working btw???");
            }
            
            RobotGamePieceController.SetPreload(coralArmState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[]
            {
                coralHandoffState,
                coralArmState,
            };
            _coralController.intakes.Add(coralIntake);

            _algaeController.gamePieceStates = new[] { algaeStowState };
            _algaeController.intakes.Add(algaeIntake);
            
            rollerSource.clip = rollerClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;
        }

        private void PlaceCoral()
        {
            switch (LastSetpoint)
            {
                case ReefscapeSetpoints.L4:
                    SetSetpoint(l4place);
                    _coralController.ReleaseGamePieceWithForce(l4OuttakeForce.vector3);
                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(l3place);
                    _coralController.ReleaseGamePieceWithForce(l3OuttakeForce.vector3);
                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(l2place);
                    _coralController.ReleaseGamePieceWithForce(l2OuttakeForce.vector3);
                    break;
                case ReefscapeSetpoints.L1:
                    _coralController.ReleaseGamePieceWithForce(l1OuttakeForce.vector3);
                    break;
            }
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            bool hasAlgae = _algaeController.HasPiece();
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Place:
                    if (hasCoral) PlaceCoral();
                    else _algaeController.ReleaseGamePieceWithForce(algaeOuttakeForce.vector3);
                    break;
                case ReefscapeSetpoints.Stow:
                    SetSetpoint(hasAlgae ? algaeStow : stow);
                    break;
                case ReefscapeSetpoints.Intake:
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae && !hasCoral && !hasAlgae)
                    {
                        SetSetpoint(groundAlgae);
                        _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed());
                        _coralController.RequestIntake(coralIntake, false);
                    } 
                    else if (!hasCoral && !hasAlgae)
                    {
                        SetSetpoint(stow);
                        _coralController.RequestIntake(coralIntake, !hasAlgae);
                        _algaeController.RequestIntake(algaeIntake, false);
                    }

                    if (hasAlgae || hasCoral)
                    {
                        _coralController.RequestIntake(coralIntake, false);
                        _algaeController.RequestIntake(algaeIntake, false);
                    }
                    break;
                case ReefscapeSetpoints.L1:
                    SetSetpoint(l1);
                    break;
                case ReefscapeSetpoints.Stack:
                    SetSetpoint(lollipop);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !hasAlgae);
                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(l2);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    SetSetpoint(lowAlgae);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !hasAlgae);
                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(l3);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetSetpoint(highAlgae);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !hasAlgae);
                    break;
                case ReefscapeSetpoints.L4:
                    SetSetpoint(l4);
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processor);
                    break;
                case ReefscapeSetpoints.Barge:
                    SetSetpoint(IsFacingBarge() ? bargeFront : bargeBack);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Climb:
                    climb.Climb();
                    SetClimb(climberDeployAngle);
                    SetFunnelAngle(funnelClimbAngle);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetClimb(climbClimbedAngle);
                    climb.NotClimbing();
                    break;
            }
            
            if (_coralController.atTarget && _coralController.currentStateNum == coralHandoffState.stateNum)
            {
                SetSetpoint(coralPickup);
                if (Utils.InRange(coralPickup.elevatorHeight, elevator.GetElevatorHeight(), .25f))
                {
                    _coralController.SetTargetState(coralArmState);
                    SetSetpoint(stow);
                }
            }
            else if (!hasCoral)
            {
                _coralController.SetTargetState(coralHandoffState);
            }
            _algaeController.SetTargetState(algaeStowState);
            
            if (CurrentSetpoint != ReefscapeSetpoints.Climb)
            {
                climb.NotClimbing();
            }
            
            if (climbScorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climb)// && climberObject.WingsOpen())
            {
                climb.PlayClick();
                SetState(ReefscapeSetpoints.Climbed);
            }
            else if (!climbScorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climbed)
                SetState(ReefscapeSetpoints.Climb);
            
            if (_mainRb != null)
            {
                if (CurrentSetpoint == ReefscapeSetpoints.Climbed)
                {
                    if (!_isCgShifted)
                    {
                        _mainRb.centerOfMass = new Vector3(climbedCenterOfMassX, _originalCenterOfMass.y, climbedCenterOfMassZ);
                        _isCgShifted = true;
                    }
                }
                else if (_isCgShifted)
                {
                    _mainRb.centerOfMass = _originalCenterOfMass;
                    _isCgShifted = false;
                }
            }
            
            UpdateSetpoints();
            Audio();
        }
        
        private void Audio()
        {
            var a = soundDetector.OverlapBox(coralMask);
            if (a.Length > 0)
            {
                if (canClack && !funnelCloseSource.isPlaying && !_coralController.atTarget)
                {
                    funnelCloseSource.Play();
                    canClack = false;
                }
            }
            else
            {
                canClack = true;
            }
            
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
                }

                return;
            }

            if (_coralController.HasPiece() &&
                !(_coralController.atTarget && _coralController.currentStateNum == coralArmState.stateNum))
            {
                RunRollers();
                if (!rollerSource.isPlaying)
                {
                    rollerSource.Play();
                }
            }
            else if (IntakeAction.IsPressed() && !_algaeController.atTarget &&
                     (CurrentSetpoint == ReefscapeSetpoints.HighAlgae ||
                      CurrentSetpoint == ReefscapeSetpoints.LowAlgae ||
                      CurrentSetpoint == ReefscapeSetpoints.Stack ||
                      (CurrentSetpoint == ReefscapeSetpoints.Intake && CurrentRobotMode == ReefscapeRobotMode.Algae)))
            {
                RunRollers();
                if (!rollerSource.isPlaying)
                {
                    rollerSource.Play();
                }
            }
            else if (CurrentSetpoint == ReefscapeSetpoints.Place && OuttakeAction.IsPressed())
            {
                RunRollers(true);
                if (!rollerSource.isPlaying)
                {
                    rollerSource.Play();
                }
            }
            else
            {
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
                }
            }
        }

        private void RunRollers(bool reverse = false)
        {
            foreach (var roller in armRollers)
            {
                roller.VelocityRoller(reverse ? rollerSpeed : -rollerSpeed);
            }
        }

        private void LateUpdate()
        {
            funnelJoint.UpdatePid(funnelPid);
            armJoint.UpdatePid(armPid);
            climberJoint.UpdatePid(climberPid);
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            armJoint.SetTargetAngle(_armAngle).withAxis(JointAxis.X).noWrap(-90);
            funnelJoint.SetTargetAngle(_funnelAngle).withAxis(JointAxis.X);
            climberJoint.SetTargetAngle(_climberAngle).withAxis(JointAxis.X).noWrap(180);
        }

        private void SetClimb(float angle)
        {
            _climberAngle = angle;
        }

        private void SetSetpoint(AlphabotsSetpoint setpoint)
        {
            if (setpoint == l4 || setpoint == l3 || setpoint == l2 || setpoint == l1)
            {
                if (!(_coralController.atTarget && _coralController.currentStateNum == coralArmState.stateNum))
                {
                    return;
                }
            }

            if (setpoint == bargeBack || setpoint == bargeFront)
            {
                if (Utils.InRange(setpoint.elevatorHeight, elevator.GetElevatorHeight(), 2f))
                {
                    _armAngle = setpoint.armAngle;
                }
                _elevatorTargetHeight = setpoint.elevatorHeight;
                return;
            }
            
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _armAngle = setpoint.armAngle;
        }

        private void SetFunnelAngle(float angle)
        {
            _funnelAngle = angle;
        }
        
        private bool IsFacingBarge()
        {
            var toZAxisXY = new UnityEngine.Vector3(-transform.position.x, -transform.position.y, 0f).normalized;
            var forwardXY = new UnityEngine.Vector3(transform.forward.x, transform.forward.y, 0f).normalized;
            var dot = UnityEngine.Vector3.Dot(forwardXY, toZAxisXY);
            return dot > 0.0f;
        }
    }
}