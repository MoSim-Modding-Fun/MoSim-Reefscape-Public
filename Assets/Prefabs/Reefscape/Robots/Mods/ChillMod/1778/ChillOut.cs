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
using Unity.VisualScripting;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.ChillMod._1778
{
    public class ChillOut: ReefscapeRobotBase
    {
        [Header("Joints")]
        [SerializeField] private GenericElevator elevator;

        [SerializeField] private GenericJoint arm;
        [SerializeField] private PidConstants armPid;
        
        [SerializeField] private GenericJoint intake;
        [SerializeField] private PidConstants intakePid;

        private float _elevatorTargetHeight;
        private float _armTargetAngle;
        private float _intakeTargetAngle;
        
        [Header("Setpoints")]
        [SerializeField] private ChillOutSetpoint stow;
        [SerializeField] private ChillOutSetpoint stowAlgae;
        [SerializeField] private ChillOutSetpoint intakeOut;
        [SerializeField] private ChillOutSetpoint intakeOutAlgae;
        [SerializeField] private ChillOutSetpoint coralTransferring;
        
        [SerializeField] private ChillOutSetpoint l4Front;
        [SerializeField] private ChillOutSetpoint l4Back;
        [SerializeField] private ChillOutSetpoint l3Front;
        [SerializeField] private ChillOutSetpoint l3Back;
        [SerializeField] private ChillOutSetpoint l2Front;
        [SerializeField] private ChillOutSetpoint l2Back;
        
        [SerializeField] private ChillOutSetpoint l1;
        
        [SerializeField] private ChillOutSetpoint groundAlgae;
        [SerializeField] private ChillOutSetpoint lolli;
        [SerializeField] private ChillOutSetpoint lowFront;
        [SerializeField] private ChillOutSetpoint lowBack;
        [SerializeField] private ChillOutSetpoint highFront;
        [SerializeField] private ChillOutSetpoint highBack;
        [SerializeField] private ChillOutSetpoint process;
        [SerializeField] private ChillOutSetpoint barge1;
        [SerializeField] private ChillOutSetpoint barge2;
        
        [Header("Intake and Stow States")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake armCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;

        [SerializeField] private GamePieceState coralIntakeState;
        [SerializeField] private GamePieceState coralStowState;
        [SerializeField] private GamePieceState algaeStowState;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;
        
        [Header("Align Offsets")]
        [SerializeField] private AutoAlignOffset frontLeft;
        [SerializeField] private AutoAlignOffset frontRight;
        [SerializeField] private AutoAlignOffset backLeft;
        [SerializeField] private AutoAlignOffset backRight;
        
        [Header("Roller Stuff")]
        [SerializeField] private GenericRoller intakeRoller;

        private bool intaking;
        
        private ReefscapeAutoAlign align;

        private bool transferOnce = false;
        private bool intk = false;
        private bool transferring = false;
        private bool coralInPossesion = false;

        private bool l1once = false;

        [SerializeField] private float ElevatorLowerHeight;
        [SerializeField] private float ArmLowerHeight;

        private bool placed = false;
        
        private bool placeOnce = false;

        [SerializeField] private Collider[] scoop;

        private ReefscapeSetpoints nextLevel = ReefscapeSetpoints.Stow;

        [SerializeField] private GenericAnimationJoint[] intakeRollers;
        [SerializeField] private GenericAnimationJoint[] eeRollers;
        
        [Header("Algae Stall Audio")]
        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallAudio;
        
        [Header("Intake and EE Rollers")]
        [SerializeField] private AudioSource intakeAudio;
        [SerializeField] private AudioSource eeAudio;
        [SerializeField] private AudioClip rollerAudio;
        
        protected override void Start()
        {
            base.Start();
            
            align = gameObject.GetComponent<ReefscapeAutoAlign>();
            
            arm.SetPid(armPid);
            intake.SetPid(intakePid);

            _elevatorTargetHeight = 0;
            _armTargetAngle = 0;
            _intakeTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[]
            {
                coralIntakeState,
                coralStowState
            };
            _coralController.intakes.Add(armCoralIntake);
            _coralController.intakes.Add(coralIntake);
            
            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
            algaeStallSource.clip = algaeStallAudio;
            algaeStallSource.loop = true;
            algaeStallSource.Stop();
            
            intakeAudio.clip = rollerAudio;
            intakeAudio.loop = true;
            intakeAudio.Stop();
            
            eeAudio.clip = rollerAudio;
            eeAudio.loop = true;
            eeAudio.Stop();
        }

        private bool atSetpoint(ChillOutSetpoint stp)
        {
            return
                Utils.InRange(elevator.GetElevatorHeight(), stp.elevatorHeight, 2f) &&
                Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), stp.armAngle, 2f) &&
                Utils.InAngularRange(intake.GetSingleAxisAngle(JointAxis.X), stp.intakeAngle, 2f);
        }
        
        public bool atSetpoint(ChillOutSetpoint stp, GenericJoint jnt)
        {
            return Utils.InAngularRange(jnt.GetSingleAxisAngle(JointAxis.X), stp.elevatorHeight, 2f);
        }
        
        public bool atSetpoint(ChillOutSetpoint stp, GenericElevator elv)
            {
                return Utils.InRange(elv.GetElevatorHeight(), stp.elevatorHeight, 2f);
            }
        
        public bool armAtTargetAngle()
        {
            return Utils.InRange(arm.GetSingleAxisAngle(JointAxis.X), _armTargetAngle, 2f);
        }


        private void setIntakeIntake()
        {
            intakeRoller.SetAngularVelocity(4000);
            foreach (var col in scoop)
            {
                col.enabled = true;
            }
        }

        private void setIntakeOuttaking()
        {
            intakeRoller.SetAngularVelocity(-3000);
            foreach (var col in scoop)
            {
                col.enabled = false;
            }
        }
        
        private void setIntakeOuttaking(float s)
        {
            intakeRoller.SetAngularVelocity(-s);
            foreach (var col in scoop)
            {
                col.enabled = false;
            }
        }

        private void FixedUpdate()
        {
            RunAudio();
            
            bool hasAlgae = _algaeController.HasPiece();
            bool hasCoral = _coralController.HasPiece();
            bool intakeHasCoral = _coralController.atTarget && _coralController.currentStateNum == coralIntakeState.stateNum;
            bool armHasCoral = _coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum;
            
            _algaeController.SetTargetState(algaeStowState);
            
            if (!IntakeAction.IsPressed())
            {
                _algaeController.RequestIntake(algaeIntake, false);
                _coralController.RequestIntake(coralIntake, false);
                transferOnce = false;
            }

            if (intakeHasCoral || transferring)
            {
                coralInPossesion = true;
            }

            if (hasCoral && CurrentSetpoint != ReefscapeSetpoints.L1 && CurrentIntakeMode != ReefscapeIntakeMode.L1)
            {
                setIntakeOuttaking(0);
            }

            if (CurrentSetpoint == ReefscapeSetpoints.L1 || LastSetpoint == ReefscapeSetpoints.L1 && CurrentSetpoint != ReefscapeSetpoints.Intake && CurrentSetpoint != ReefscapeSetpoints.Stow)
            {
                setIntakeOuttaking();
            }
            else
            {
                setIntakeIntake();
            }

            if (armHasCoral)
            {
                _coralController.RequestIntake(armCoralIntake, false);
            }

            if (CurrentIntakeMode == ReefscapeIntakeMode.Normal)
            {
                if (intakeHasCoral && atSetpoint(stow))
                {
                    transferring = true;
                } 
                else if (armHasCoral && transferring)
                {
                    transferring = false;
                    SetState(nextLevel);
                }
            }
        
            if (CurrentSetpoint != ReefscapeSetpoints.Place) {
                placeOnce = false;
            }

            if (atSetpoint(coralTransferring) && !transferOnce && CurrentIntakeMode == ReefscapeIntakeMode.Normal && transferring)
            {
                transferToArm();
                transferOnce = true;
                intk = false;
            }
            else if (!hasCoral && !atSetpoint(l1))
            {
                setIntakeIntake();
            }

            if (atSetpoint(coralTransferring) && !(CurrentIntakeMode == ReefscapeIntakeMode.L1 || CurrentSetpoint == ReefscapeSetpoints.L1))
            {
                setIntakeRollers(-20);
                setEndEffectorRollers(20);
            } else if (atSetpoint(coralTransferring))
            {
                setIntakeRollers(20);
                setEndEffectorRollers(-20);
            }

            if (CurrentIntakeMode == ReefscapeIntakeMode.Normal && intk)
            {
                _coralController.SetTargetState(coralIntakeState);
            }
            else if (CurrentIntakeMode == ReefscapeIntakeMode.Normal && CurrentSetpoint !=  ReefscapeSetpoints.L1 && !hasCoral)
            {
                _coralController.SetTargetState(coralStowState);
            }
            else if (CurrentIntakeMode == ReefscapeIntakeMode.L1 || CurrentSetpoint ==  ReefscapeSetpoints.L1 & !hasCoral)
            {
                _coralController.SetTargetState(coralIntakeState);
            }

            if (transferring)
            {
                if (L4Action.IsPressed())
                {
                    nextLevel = ReefscapeSetpoints.L4;
                }
                if (L3Action.IsPressed())
                {
                    nextLevel = ReefscapeSetpoints.L3;
                }
                if (L2Action.IsPressed())
                {
                    nextLevel = ReefscapeSetpoints.L2;
                }

            }

            if (CurrentSetpoint != ReefscapeSetpoints.L1)
            {
                l1once = false;
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {
                placed = false;
            }

            if (!IntakeAction.IsPressed() && !OuttakeAction.IsPressed())
            {
                intakeRollersStop();
                endEffectorRollersStop();
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {
                intakeRollersStop();
                endEffectorRollersStop();
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    if (CurrentIntakeMode == ReefscapeIntakeMode.Normal && (intakeHasCoral || coralInPossesion) && transferring)
                    {
                        SetSetpoint(coralTransferring);
                    }
                    else
                    {
                        SetSetpoint(hasAlgae ? stowAlgae : stow);
                    }

                    if (!transferring)
                    {
                        intakeRollersStop();
                        endEffectorRollersStop();
                    }

                    _algaeController.RequestIntake(algaeIntake, false);
                    _coralController.RequestIntake(coralIntake, !transferring && atSetpoint(stow));
                    break;
                case ReefscapeSetpoints.Intake:
                    if ((CurrentRobotMode == ReefscapeRobotMode.Coral ||
                        hasAlgae) && !hasCoral)
                    {
                        SetSetpoint(hasAlgae ? intakeOutAlgae : intakeOut);
                    }

                    if (CurrentRobotMode == ReefscapeRobotMode.Algae && !armHasCoral && !hasAlgae)
                    {
                        SetSetpoint(groundAlgae);
                        _coralController.RequestIntake(coralIntake, false);
                        setIntakeOuttaking(0);
                    }
                    
                    _algaeController.RequestIntake(algaeIntake, CurrentRobotMode == ReefscapeRobotMode.Algae && !hasAlgae && !armHasCoral && IntakeAction.IsPressed());
                    if (!hasCoral && !atSetpoint(coralTransferring) && !transferOnce)
                    {
                        _coralController.RequestIntake(coralIntake, !transferring);
                        intk = true;
                    }

                    if (atSetpoint(intakeOut))
                    {
                        setIntakeRollers(50);
                    } 
                    else if (atSetpoint(intakeOutAlgae))
                    {
                        setIntakeRollers(50);
                        setEndEffectorRollers(50);
                    }
                    else
                    {
                        setEndEffectorRollers(50);
                    }

                    break;
                case ReefscapeSetpoints.Place:
                    _coralController.RequestIntake(coralIntake, false);
                    _coralController.RequestIntake(armCoralIntake, false);

                    if (!placeOnce)
                    {
                        if (LastSetpoint == ReefscapeSetpoints.L4 || LastSetpoint == ReefscapeSetpoints.L3 ||
                            LastSetpoint == ReefscapeSetpoints.L2)
                        {
                            PlaceBranch(GetSetpointByLevel());
                            setEndEffectorRollers(-20);
                        }
                        else
                        {
                            PlacePiece();
                        }
                    }

                    nextLevel = ReefscapeSetpoints.Stow;
                    break;
                case ReefscapeSetpoints.L1:
                    if (intakeHasCoral && (CurrentIntakeMode == ReefscapeIntakeMode.L1 || l1once))
                    {
                        SetSetpoint(l1);
                    }
                    else
                    {
                        if (transferring)
                        {
                            nextLevel = ReefscapeSetpoints.L1;
                            SetState(ReefscapeSetpoints.L1);
                        }
                        else
                        {
                            if (atSetpoint(coralTransferring))
                            {
                                if (armHasCoral && !l1once)
                                {
                                    //_coralController.RequestIntake(armCoralIntake, false);
                                    //_coralController.ReleaseGamePieceWithForce(new Vector3(0, 3, 0));
                                    l1once = true;
                                }

                            }
                            else
                            {
                                SetSetpoint(coralTransferring);
                            }

                            if (l1once)
                            {
                                //_coralController.RequestIntake(armCoralIntake, false);
                                setIntakeIntake();
                                _coralController.SetTargetState(coralIntakeState);
                                //_coralController.RequestIntake(coralIntake, true);
                            }
                        }
                    } 
                    // _coralController.RequestIntake(coralIntake, true);
                    break;
                case ReefscapeSetpoints.Stack:
                    SetSetpoint(lolli);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                    _coralController.RequestIntake(coralIntake, false);
                    if (IntakeAction.IsPressed())
                    {
                        setEndEffectorRollers(50);
                    }
                    else
                    {
                        setEndEffectorRollers(0);
                    }
                    break;
                case ReefscapeSetpoints.L2:
                    if (armHasCoral)
                    {
                        SetSetpoint(!FacingReef ? l2Front : l2Back);
                    }
                    else
                    {
                        SetState(ReefscapeSetpoints.Stow);
                        nextLevel = ReefscapeSetpoints.L2;
                    }
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    if (transferring || atSetpoint(coralTransferring)) 
                    {
                        SetState(ReefscapeSetpoints.L2);
                    } else {
                        SetSetpoint(!FacingReef ? lowFront : lowBack);
                        _algaeController.RequestIntake(algaeIntake, IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                        _coralController.RequestIntake(coralIntake, false);
                        if (IntakeAction.IsPressed())
                        {
                            setEndEffectorRollers(50);
                        }
                        else
                        {
                            setEndEffectorRollers(0);
                        }
                    }
                    break;
                case ReefscapeSetpoints.L3:
                    if (armHasCoral)
                    {
                        SetSetpoint(!FacingReef ? l3Front : l3Back);
                    }
                    else
                    {
                        SetState(ReefscapeSetpoints.Stow);
                        nextLevel = ReefscapeSetpoints.L3;
                    }
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    if (transferring || atSetpoint(coralTransferring)) 
                    {
                        SetState(ReefscapeSetpoints.L2);
                    }
                    else
                    {
                        SetSetpoint(!FacingReef ? highFront : highBack);
                        _algaeController.RequestIntake(algaeIntake,
                            IntakeAction.IsInProgress() && !hasAlgae && !hasCoral);
                        _coralController.RequestIntake(coralIntake, false);
                        if (IntakeAction.IsPressed())
                        {
                            setEndEffectorRollers(50);
                        }
                        else
                        {
                            setEndEffectorRollers(0);
                        }
                    }

                    break;
                case ReefscapeSetpoints.L4:
                    if (armHasCoral)
                    {
                        SetSetpoint(!FacingReef ? l4Front : l4Back);
                    }
                    else if (hasAlgae)
                    {
                        SetState(ReefscapeSetpoints.Barge);
                    }
                    else
                    {
                        SetState(ReefscapeSetpoints.Stow);
                        nextLevel = ReefscapeSetpoints.L4;
                    }
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(process);
                    break;
                case ReefscapeSetpoints.Barge:
                    SetSetpoint(FacingReef ? barge1 : barge2);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Climb:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                if (L4Action.IsPressed())
                {
                    nextLevel =  ReefscapeSetpoints.L4;
                    IntakeModeToggleAction.Enable();
                    IntakeModeToggleAction.Disable();
                    SetState(ReefscapeSetpoints.Stow);
                }
                else if (L3Action.IsPressed())
                {
                    nextLevel =  ReefscapeSetpoints.L3;
                    IntakeModeToggleAction.Enable();
                    IntakeModeToggleAction.Disable();
                    SetState(ReefscapeSetpoints.Stow);
                }
                else if (L2Action.IsPressed())
                {
                    nextLevel =  ReefscapeSetpoints.L2;
                    IntakeModeToggleAction.Enable();
                    IntakeModeToggleAction.Disable();
                    SetState(ReefscapeSetpoints.Stow);
                }
            }

            if (transferring)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }

            if (placeOnce)
            {
                intakeRollersStop();
                endEffectorRollersStop();
            }

            
            AutoAlignnnn();
            ApplySetpoints();
        }

        private void transferToArm()
        {
            if (_coralController.currentStateNum == coralIntakeState.stateNum && _coralController.atTarget)
            {
                //_coralController.ReleaseGamePieceWithForce(new Vector3(0, -3, 0));
                _coralController.SetTargetState(coralStowState);
            }

            //_coralController.RequestIntake(armCoralIntake, transferring); //atSetpoint(coralTransferring));
        }
        
        private void AutoAlignnnn()
        {
            if (AutoAlignLeftAction.IsPressed() && !FacingReef && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(frontLeft);
            }
            else if (AutoAlignRightAction.IsPressed() && !FacingReef && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(frontRight);
            }
            else if (AutoAlignLeftAction.IsPressed() && FacingReef && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(backLeft);
            }
            else if (AutoAlignRightAction.IsPressed() && FacingReef && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(backRight);
            }
        }

        private void SetAlignOffsets(AutoAlignOffset alignment)
        {
            align.offset = new Vector3(alignment.xOffset, alignment.yOffset, alignment.zOffset);
            align.rotation = alignment.Rotation;
        }

        private int GetLevelByState()
        {
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.L1:
                    return 1;
                case ReefscapeSetpoints.L2:
                    return 2;
                case ReefscapeSetpoints.L3:
                    return 3;
                case ReefscapeSetpoints.L4:
                    return 4;
            }
            
            switch (LastSetpoint)
            {
                case ReefscapeSetpoints.L1:
                    return 1;
                case ReefscapeSetpoints.L2:
                    return 2;
                case ReefscapeSetpoints.L3:
                    return 3;
                case ReefscapeSetpoints.L4:
                    return 4;
            }

            return 0;
        }

        private ChillOutSetpoint GetSetpointByLevel()
        {
            switch (GetLevelByState())
            {
                case 1:
                    return l1;
                case 2:
                    return !FacingReef ? l2Front : l2Back;
                case 3:
                    return !FacingReef ? l3Front : l3Back;
                case 4:
                    return !FacingReef ? l4Front : l4Back;
            }

            return null;
        }

        private void PlacePiece()
        {
			if (!placeOnce) {
            	if (_algaeController.atTarget)
            	{
                	_algaeController.ReleaseGamePieceWithForce(atSetpoint(barge1, elevator) ? new Vector3(0, 4, 0) : new Vector3(0, 2, 0));
                    setEndEffectorRollers(-20);
            	}
            	else if (CurrentIntakeMode == ReefscapeIntakeMode.L1 || LastSetpoint == ReefscapeSetpoints.L1)
            	{
                    _coralController.ReleaseGamePieceWithForce(new Vector3(4, .6f, 0));
//                    _coralController.ReleaseGamePieceWithForce(new Vector3(-1.5f, -3.8f, 0));
                	coralInPossesion = false;
                    setIntakeRollers(20);
            	}
            	else
            	{
                	_coralController.ReleaseGamePieceWithForce(new Vector3(0, 0.5f, FacingReef ? 0.5f : -0.5f));
                	// _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 0));
                	coralInPossesion = false;
                    if (_coralController.atTarget && _coralController.currentStateNum == coralIntakeState.stateNum)
                    {
                        setIntakeRollers(20);
                    }
                    else
                    {
                        setEndEffectorRollers(-20);
                    }
            	}
			}

            placeOnce = true;
        }

        private void PlaceBranch(ChillOutSetpoint setpoint)
        {
            switch (GetLevelByState())
            {
                case 4:
                    //_elevatorTargetHeight = setpoint.elevatorHeight - 2.5f;
                    //_armTargetAngle = setpoint.armAngle - (FacingReef ? 17 : -17);

                    //if (Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), _armTargetAngle, 2f))
                    //{
                    //    _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0.5f, FacingReef ? 1f: -1f));
                    //    coralInPossesion = false;
                    //}
                    //break;
                    _elevatorTargetHeight = setpoint.elevatorHeight - (ElevatorLowerHeight * 0.8f);
                    _armTargetAngle = setpoint.armAngle - (FacingReef ? (1.4f * ArmLowerHeight) : (1.4f * -ArmLowerHeight));
                    
                    if (armAtTargetAngle())
                    {
                        _coralController.ReleaseGamePieceWithForce(new Vector3(0, .5f, !FacingReef ? 2 : -2));
                        coralInPossesion = false;
                    }
                    break;
                case 3:
                    _elevatorTargetHeight = setpoint.elevatorHeight - ElevatorLowerHeight;
                    _armTargetAngle = setpoint.armAngle - (FacingReef ? ArmLowerHeight : -ArmLowerHeight);
                    
                    if (armAtTargetAngle())
                    {
                        _coralController.ReleaseGamePieceWithForce(new Vector3(0, 1, !FacingReef ? 1 : -1));
                        coralInPossesion = false;
                    }
                    break;
                case 2: 
                    _elevatorTargetHeight = setpoint.elevatorHeight - ElevatorLowerHeight;
                    _armTargetAngle = setpoint.armAngle - (FacingReef ? ArmLowerHeight : -ArmLowerHeight);
                    
                    if (armAtTargetAngle())
                    {
                        _coralController.ReleaseGamePieceWithForce(new Vector3(0, .7f, !FacingReef ? 1 : -1));
                        coralInPossesion = false;
                    }
                    break;
            }
            _intakeTargetAngle = setpoint.intakeAngle;
        }

        private void setIntakeRollers(float speed)
        {
            for(int i = 0; i < intakeRollers.Length; i++)
            {
                intakeRollers[i].VelocityRoller(5 * speed);
            }
        }

        private void intakeRollersStop()
        {
            setIntakeRollers(0);
        }
        
        private void setEndEffectorRollers(float speed)
        {
            for(int i = 0; i < eeRollers.Length; i++)
            {
                eeRollers[i].VelocityRoller(5 * speed);
            }
        }

        private void endEffectorRollersStop()
        {
            setEndEffectorRollers(0);
        }

        private void RunAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (intakeAudio.isPlaying || eeAudio.isPlaying || algaeStallSource.isPlaying)
                {
                    intakeAudio.Stop();
                    eeAudio.Stop();
                    algaeStallSource.Stop();
                }

                return;
            }
            if (_algaeController.atTarget && !algaeStallSource.isPlaying)
            {
                algaeStallSource.Play();
            }

            if (!_algaeController.atTarget)
            {
                algaeStallSource.Stop();
            }

            
            if (IntakeAction.IsPressed())
            {
                if (CurrentSetpoint == ReefscapeSetpoints.HighAlgae || CurrentSetpoint == ReefscapeSetpoints.LowAlgae ||
                    CurrentSetpoint == ReefscapeSetpoints.Stack ||
                    (CurrentSetpoint == ReefscapeSetpoints.Intake && CurrentRobotMode == ReefscapeRobotMode.Algae))
                {
                    if (!eeAudio.isPlaying)
                    {
                        eeAudio.Play();
                    }
                }
                
                else if (CurrentSetpoint == ReefscapeSetpoints.Intake && !atSetpoint(stow, intake))
                {
                    if (!intakeAudio.isPlaying)
                    {
                        intakeAudio.Play();
                    }
                }
            }
            else if (OuttakeAction.IsPressed())
            {
                if (_coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum)
                {
                    if (!eeAudio.isPlaying)
                    {
                        eeAudio.Play();
                    }
                }
                
                else if (_algaeController.atTarget)
                {
                    if (!eeAudio.isPlaying)
                    {
                        eeAudio.Play();
                    }
                }
                
                else if (_coralController.atTarget && _coralController.currentStateNum == coralIntakeState.stateNum)
                {
                    if (!intakeAudio.isPlaying)
                    {
                        intakeAudio.Play();
                    }
                }
            } 
            else if (transferring)
            {
                if (!eeAudio.isPlaying)
                {
                    eeAudio.Play();
                }
                if (!intakeAudio.isPlaying)
                {
                    intakeAudio.Play();
                }
            }
            else if (!OuttakeAction.IsPressed() && !OuttakeAction.IsPressed() && !transferring)
            {
                if (intakeAudio.isPlaying)
                {
                    intakeAudio.Stop();
                }
                
                if (eeAudio.isPlaying)
                {
                    eeAudio.Stop();
                }
            }
        }

        private void SetSetpoint(ChillOutSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _armTargetAngle = setpoint.armAngle;
            _intakeTargetAngle = setpoint.intakeAngle;
        }

        private bool isCurrentSetpoint(ChillOutSetpoint setpoint)
        {
            return
                _elevatorTargetHeight == setpoint.elevatorHeight &&
                _armTargetAngle == setpoint.armAngle &&
                _intakeTargetAngle == setpoint.intakeAngle;
        }
        
        private void ApplySetpoints() 
        {
            elevator.SetTarget(_elevatorTargetHeight);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X).noWrap(
                (((CurrentRobotMode == ReefscapeRobotMode.Algae &&
                   _algaeController.atTarget) || 
                  CurrentSetpoint == ReefscapeSetpoints.HighAlgae ||
                  CurrentSetpoint == ReefscapeSetpoints.LowAlgae || 
                  LastSetpoint == ReefscapeSetpoints.HighAlgae ||
                  LastSetpoint == ReefscapeSetpoints.LowAlgae || 
                  CurrentSetpoint == ReefscapeSetpoints.Stack ||
                  LastSetpoint == ReefscapeSetpoints.Stack ||
                  LastSetpoint == ReefscapeSetpoints.Place) || 
                 _algaeController.atTarget)
                    ? 180
                    : (!FacingReef ? 150 : 210));
            intake.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.X).noWrap(-90);
        }
        
    }
    
}
