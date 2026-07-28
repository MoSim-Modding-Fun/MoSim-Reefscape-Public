using Games.Reefscape.Scoring.Scorers;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;
using UnityEngine.Serialization;

namespace Prefabs.Reefscape.Robots.Mods.NYModpack._694
{
    public class StuyV3Climber : MonoBehaviour
    {
        private ClimbScorer _climbScorer;
        
        [Header("Clicker Joints")]
        [SerializeField] private GenericAnimationJoint clickerL;
        [SerializeField] private GenericAnimationJoint clickerR;
        [SerializeField] private GenericAnimationJoint clickerL1;
        [SerializeField] private GenericAnimationJoint clickerR1;

        [Header("Climber Joints")]
        [SerializeField] private GenericJoint intakeWheelL;
        [SerializeField] private GenericJoint intakeWheelR;
    
        [FormerlySerializedAs("pidmi")] [SerializeField] private PidConstants pidConstants;
        
        [Header("Climber Wheels")]
        [SerializeField] private GameObject intakeWheelGameObjectL;
        [SerializeField] private GameObject intakeWheelGameObjectR;
        [SerializeField] private float targetIntakeWheelSpeed = 100f;
        private float _intakeWheelSpeed;
    
        [SerializeField] private float climbingAngularVelocity = 40f;
        private float _angularVelocity;

        [SerializeField] private float ClickerSpeed = 720f;
        
        [Header("Audio")]
        [SerializeField] private AudioSource rollerAudioSource;
        [SerializeField] private AudioClip rollerClip;

        [SerializeField] private AudioSource clickSource;
        [SerializeField] private AudioClip detectorClickClip;
        
        
        private void Start()
        {
            _climbScorer = GetComponentInParent<ClimbScorer>();
            if (_climbScorer == null)
            {
                Debug.LogError("StuyV3Climber: ClimbScorer component not found in parent.");
            }
            
            rollerAudioSource.clip = rollerClip; 
            rollerAudioSource.loop = true;
            rollerAudioSource.Stop();
            
            intakeWheelL.SetPid(pidConstants);
            intakeWheelR.SetPid(pidConstants);
            _angularVelocity = 0;
        }

        private void LateUpdate()
        {
        }

        // Update is called once per frame
        private void Update()
        {
            clickerL.SpringLoaded().AllowedDirection(1).RotationSpeed(ClickerSpeed);
            clickerR.SpringLoaded().AllowedDirection(1).RotationSpeed(ClickerSpeed);
        
            clickerL1.SpringLoaded().AllowedDirection(1).RotationSpeed(ClickerSpeed);
            clickerR1.SpringLoaded().AllowedDirection(1).RotationSpeed(ClickerSpeed);
        
        }

        private void FixedUpdate()
        {
            intakeWheelL.SetAngularVelocity(_angularVelocity).WithAxis(JointAxis.Y);
            intakeWheelR.SetAngularVelocity(-_angularVelocity).WithAxis(JointAxis.Y);
            intakeWheelGameObjectL.transform.Rotate(Vector3.left, _intakeWheelSpeed * Time.fixedDeltaTime);
            intakeWheelGameObjectR.transform.Rotate(Vector3.left, -_intakeWheelSpeed * Time.fixedDeltaTime);
        
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
                if (_intakeWheelSpeed != 0 && !rollerAudioSource.isPlaying) rollerAudioSource.Play();
                else if (_intakeWheelSpeed == 0 && rollerAudioSource.isPlaying) rollerAudioSource.Stop();
            }
        }

        public void Climb()
        {
            _angularVelocity = climbingAngularVelocity;
            _intakeWheelSpeed = targetIntakeWheelSpeed;
        }

        public void PlayClick()
        {
            if (clickSource != null && detectorClickClip != null)
            {
                clickSource.PlayOneShot(detectorClickClip);
            }
        }
        
        public bool WingsOpen()
        {
            var result = (Utils.InAngularRange(clickerL1.transform.localEulerAngles.y, 0, 3) &&
                          Utils.InAngularRange(clickerR1.transform.localEulerAngles.y, 0, 3));
            return result;
        }

        public void NotClimbing()
        {
            _angularVelocity = 0;
            _intakeWheelSpeed = 0;
        }
    }
}
