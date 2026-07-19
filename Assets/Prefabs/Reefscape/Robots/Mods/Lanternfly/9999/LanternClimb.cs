using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Lanternfly._9999
{
    public class LanternClimb : MonoBehaviour
    {
        [Header("Climber Joints")]
        [SerializeField] private GenericJoint climberArm; 
        [SerializeField] private GenericJoint intakeWheelL;
        [SerializeField] private GenericJoint intakeWheelR;
        
        [Header("Clicker Joints")]
        [SerializeField] private GenericAnimationJoint clickerL;
        [SerializeField] private GenericAnimationJoint clickerR;
        [SerializeField] private GenericAnimationJoint clickerL1;
        [SerializeField] private GenericAnimationJoint clickerR1;

        [Header("PIDs & Axes")]
        [SerializeField] private PidConstants armPid;
        [SerializeField] private PidConstants wheelPid;
        [SerializeField] private JointAxis armAxis = JointAxis.X;
        [SerializeField] private JointAxis wheelAxis = JointAxis.Y;

        [Header("Setpoints")]
        [Tooltip("Angle when the robot is driving around normally.")]
        [SerializeField] private float stowAngle = 0;
        [Tooltip("Angle when the arm reaches up to grab the cage.")]
        [SerializeField] private float deployAngle = -90f;
        [Tooltip("Angle when the arm pulls the robot up.")]
        [SerializeField] private float retractAngle = 0f;

        [Header("Settings")]
        [SerializeField] private float targetIntakeWheelSpeed = 125f;
        [SerializeField] private float climbingAngularVelocity = 60f; 

        // ----------------------------------------------------
        // AUDIO COMPONENTS
        // ----------------------------------------------------
        [Header("Audio (Looping)")]
        [SerializeField] private AudioSource rollerAudioSource;
        [SerializeField] private AudioClip rollerClip;
        [Range(0f, 1f)] [SerializeField] private float volRoller = 0.5f; 

        [Header("Audio (One-Shot Events)")]
        [SerializeField] private AudioSource oneShotAudioSource;
        
        [SerializeField] private AudioClip detectorClickClip;
        [Range(0f, 1f)] [SerializeField] private float volClick = 0.5f; 
        
        [SerializeField] private AudioClip deployArmClip;
        [Range(0f, 1f)] [SerializeField] private float volDeploy = 0.8f; 
        
        [SerializeField] private AudioClip pullDownClip;
        [Range(0f, 1f)] [SerializeField] private float volPullDown = 0.8f; 
        // ----------------------------------------------------

        private float _intakeWheelSpeed;
        private float _angularVelocity;
        private float _armTarget = -86f; 
        private bool _isRetracting = false; 
        
        private void Start()
        {
            if (climberArm != null) climberArm.SetPid(armPid);
            if (intakeWheelL != null) intakeWheelL.SetPid(wheelPid);
            if (intakeWheelR != null) intakeWheelR.SetPid(wheelPid);

            if (rollerAudioSource != null && rollerClip != null) 
            { 
                rollerAudioSource.clip = rollerClip; 
                rollerAudioSource.loop = true; 
                rollerAudioSource.volume = volRoller;
            }

            _armTarget = stowAngle; // Initialize to stow
        }

        private void LateUpdate()
        {
            if (climberArm != null) climberArm.UpdatePid(armPid);
            if (intakeWheelL != null) intakeWheelL.UpdatePid(wheelPid);
            if (intakeWheelR != null) intakeWheelR.UpdatePid(wheelPid);
        }

        private void Update()
        {
            clickerL.SpringLoaded().AllowedDirection(1).RotationSpeed(150);
            clickerR.SpringLoaded().AllowedDirection(1).RotationSpeed(150);
        
            clickerL1.SpringLoaded().AllowedDirection(1).RotationSpeed(150);
            clickerR1.SpringLoaded().AllowedDirection(1).RotationSpeed(150);
        }

        public bool WingsOpen()
        {
            var result = (Utils.InAngularRange(clickerL1.transform.localEulerAngles.y, 0, 3) &&
                          Utils.InAngularRange(clickerR1.transform.localEulerAngles.y, 0, 3));
            return result;
        }
        
        private void FixedUpdate()
        {
            if (climberArm != null)
            {
                climberArm.SetTargetAngle(_armTarget).withAxis(armAxis);

                if (_isRetracting && Mathf.Abs(climberArm.GetSingleAxisAngle(armAxis) - retractAngle) <= 2f)
                {
                    _isRetracting = false; 
                    
                    Joint existingJoint = climberArm.GetComponent<Joint>();
                    if (existingJoint != null && climberArm.GetComponent<FixedJoint>() == null)
                    {
                        FixedJoint trueWeld = climberArm.gameObject.AddComponent<FixedJoint>();
                        trueWeld.connectedBody = existingJoint.connectedBody;
                    }
                }
            }
            
            if (intakeWheelL != null) intakeWheelL.SetAngularVelocity(-_angularVelocity).WithAxis(wheelAxis);
            if (intakeWheelR != null) intakeWheelR.SetAngularVelocity(_angularVelocity).WithAxis(wheelAxis);
            
            UpdateAudio();
        }

        private void UpdateAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerAudioSource != null && rollerAudioSource.isPlaying) rollerAudioSource.Stop();
                return;
            }

            if (rollerAudioSource != null)
            {
                // Don't want the audio on when stowed
                bool rollersSpinning = Mathf.Abs(_angularVelocity) > 0.1f && _armTarget == deployAngle;
                
                if (rollersSpinning && !rollerAudioSource.isPlaying) rollerAudioSource.Play();
                else if (!rollersSpinning && rollerAudioSource.isPlaying) rollerAudioSource.Stop();
            }
        }

        // Looking back this didn't really help...
        private void RemoveWeld()
        {
            if (climberArm != null)
            {
                FixedJoint weld = climberArm.GetComponent<FixedJoint>();
                if (weld != null) Destroy(weld);
            }
        }

        public void Climb()
        {
            if (_armTarget == deployAngle) return;

            RemoveWeld(); 
            if (climberArm != null) climberArm.freeAngularAxis(armAxis);

            _armTarget = deployAngle; 
            _angularVelocity = climbingAngularVelocity;
            _intakeWheelSpeed = targetIntakeWheelSpeed;
            _isRetracting = false;

            if (oneShotAudioSource != null && deployArmClip != null)
            {
                oneShotAudioSource.PlayOneShot(deployArmClip, volDeploy);
            }
        }

        public void NotClimbing()
        {
            if (_armTarget == stowAngle) return;

            RemoveWeld(); 
            if (climberArm != null) climberArm.freeAngularAxis(armAxis);

            _armTarget = stowAngle; 
            _angularVelocity = 0;
            _intakeWheelSpeed = 0;
            _isRetracting = false;
        }

        public void PlayClick()
        {
            if (oneShotAudioSource != null && detectorClickClip != null)
            {
                oneShotAudioSource.PlayOneShot(detectorClickClip);
            }
        }

        public void RetractArm()
        {
            if (_isRetracting || _armTarget == retractAngle) return;

            _armTarget = retractAngle; 
            _angularVelocity = climbingAngularVelocity; 
            _intakeWheelSpeed = targetIntakeWheelSpeed;
            _isRetracting = true;
            
            if (oneShotAudioSource != null && pullDownClip != null)
            {
                oneShotAudioSource.PlayOneShot(pullDownClip, volPullDown);
            }
        }
    }
}