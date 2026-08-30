using Games.Reefscape.Scoring.Scorers;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;
using UnityEngine.Serialization;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    public class NomadV2Climber : MonoBehaviour
    {
        private ClimbScorer _climbScorer;

        [Header("Clicker Joints")]
        [SerializeField] private GenericAnimationJoint clickerL;
        [SerializeField] private GenericAnimationJoint clickerR;
        [SerializeField] private GenericAnimationJoint clickerL1;
        [SerializeField] private GenericAnimationJoint clickerR1;

        [Header("Climber Joints")]
        [SerializeField] private GenericJoint armElevator;

        [SerializeField] private GenericJoint intakeWheelL;
        [SerializeField] private GenericJoint intakeWheelR;

        [FormerlySerializedAs("pidmi")]
        [SerializeField] private PidConstants pidConstants;

        [Header("Climber Wheels")]
        [SerializeField] private GameObject intakeWheelGameObjectL;
        [SerializeField] private GameObject intakeWheelGameObjectR;
        [SerializeField] private float targetIntakeWheelSpeed = 100f;

        private float _intakeWheelSpeed;

        [SerializeField] private float climbingAngularVelocity = 40f;
        private float _angularVelocity;

        [SerializeField] private float ClickerSpeed = 720f;

        private void Start()
        {
            _climbScorer = GetComponentInParent<ClimbScorer>();

            if (_climbScorer == null)
            {
                Debug.LogError(
                    "NomadV2Climber: ClimbScorer component not found in parent."
                );
            }

            intakeWheelL.SetPid(pidConstants);
            intakeWheelR.SetPid(pidConstants);

            // Climber arm is fixed on this robot.
            if (armElevator != null)
            {
                armElevator.lockAllAxis();
            }

            _angularVelocity = 0f;
            _intakeWheelSpeed = 0f;
        }

        private void Update()
        {
            clickerL
                .SpringLoaded()
                .AllowedDirection(1)
                .RotationSpeed(ClickerSpeed);

            clickerR
                .SpringLoaded()
                .AllowedDirection(1)
                .RotationSpeed(ClickerSpeed);

            clickerL1
                .SpringLoaded()
                .AllowedDirection(1)
                .RotationSpeed(ClickerSpeed);

            clickerR1
                .SpringLoaded()
                .AllowedDirection(1)
                .RotationSpeed(ClickerSpeed);
        }

        private void FixedUpdate()
        {
            // No armElevator movement.
            // The climber stays physically fixed.

            intakeWheelL
                .SetAngularVelocity(_angularVelocity)
                .WithAxis(JointAxis.Y);

            intakeWheelR
                .SetAngularVelocity(-_angularVelocity)
                .WithAxis(JointAxis.Y);

            if (intakeWheelGameObjectL != null)
            {
                intakeWheelGameObjectL.transform.Rotate(
                    Vector3.up,
                    -_intakeWheelSpeed * Time.fixedDeltaTime
                );
            }

            if (intakeWheelGameObjectR != null)
            {
                intakeWheelGameObjectR.transform.Rotate(
                    Vector3.up,
                    _intakeWheelSpeed * Time.fixedDeltaTime
                );
            }
        }

        public void Climb()
        {
            // The climber arm does NOT extend.
            // Only run the climb wheels.

            _angularVelocity = climbingAngularVelocity;
            _intakeWheelSpeed = targetIntakeWheelSpeed;
        }

        public bool WingsOpen()
        {
            return
                Utils.InAngularRange(
                    clickerL1.transform.localEulerAngles.y,
                    0,
                    3
                ) &&
                Utils.InAngularRange(
                    clickerR1.transform.localEulerAngles.y,
                    0,
                    3
                );
        }

        public void NotClimbing()
        {
            _angularVelocity = 0f;
            _intakeWheelSpeed = 0f;

            // Keep the fixed climber locked.
            if (armElevator != null)
            {
                armElevator.lockAllAxis();
            }
        }
    }
}
