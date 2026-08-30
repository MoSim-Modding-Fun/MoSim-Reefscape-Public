using Games.Reefscape.Enums;
using Games.Reefscape.Robots;
using RobotFramework.Controllers.Drivetrain;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    public class NomadV2ProcessorAutoAlign : MonoBehaviour
    {
        [Header("Processor Alignment")]
        [Tooltip("How far away from the processor scoring trigger the robot should stop.")]
        [SerializeField] private float processorDistance = 0.65f;

        [Tooltip("Move the final target left/right from the center of the processor opening.")]
        [SerializeField] private float sidewaysOffset = 0f;

        [Tooltip("Extra robot rotation adjustment.")]
        [SerializeField] private float rotationOffset = 0f;

        [Tooltip("Flip this if the robot drives straight AWAY from the processor.")]
        [SerializeField] private bool flipApproachDirection = false;


        [Header("Drive Tuning")]
        [SerializeField] private float translationKp = 3.5f;
        [SerializeField] private float translationKd = 0.2f;

        [SerializeField] private float rotateKp = 1.5f;
        [SerializeField] private float rotateKd = 0.1f;

        [SerializeField] private float minPower = 0.08f;


        [Header("Tolerances")]
        [SerializeField] private float translationTolerance = 0.05f;
        [SerializeField] private float rotationTolerance = 1.5f;


        private ReefscapeRobotBase _base;
        private DriveController _driveController;


        private Transform _processorA;
        private Transform _processorB;

        private Transform _scoringTriggerA;
        private Transform _scoringTriggerB;

        private Transform _allianceProcessor;
        private Transform _allianceScoringTrigger;


        private bool _processorPlaceContext;

        private bool _targetLocked;
        private bool _isLockedOn;


        private Vector2 _targetPosition;
        private float _targetRotation;


        private Vector2 _lastPosition;
        private float _lastRotation;


        private float _currentTranslationError;
        private float _currentRotationError;


        private void Start()
        {
            _base = GetComponent<ReefscapeRobotBase>();
            _driveController = GetComponent<DriveController>();

            FindProcessors();
            LockAllianceProcessor();

            _processorPlaceContext = false;
            _targetLocked = false;
            _isLockedOn = false;

            _currentTranslationError = float.MaxValue;
            _currentRotationError = float.MaxValue;
        }


        // =========================================================
        // FIND BOTH PROCESSORS
        // =========================================================

        private void FindProcessors()
        {
            GameObject processorsRoot =
                GameObject.Find("Processors");


            if (processorsRoot == null)
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Could not find Processors."
                );

                return;
            }


            foreach (
                Transform child
                in processorsRoot.transform
            )
            {
                if (child.name == "Processor")
                {
                    _processorA = child;
                }
                else if (child.name == "Processor (1)")
                {
                    _processorB = child;
                }
            }


            if (_processorA != null)
            {
                _scoringTriggerA =
                    FindChildByName(
                        _processorA,
                        "ScoringTrigger"
                    );
            }


            if (_processorB != null)
            {
                _scoringTriggerB =
                    FindChildByName(
                        _processorB,
                        "ScoringTrigger"
                    );
            }


            if (_processorA == null)
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Could not find Processor."
                );
            }


            if (_processorB == null)
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Could not find Processor (1)."
                );
            }


            if (_scoringTriggerA == null)
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Could not find first ScoringTrigger."
                );
            }


            if (_scoringTriggerB == null)
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Could not find second ScoringTrigger."
                );
            }
        }


        // =========================================================
        // FIND CHILD RECURSIVELY
        // =========================================================

        private Transform FindChildByName(
            Transform parent,
            string objectName
        )
        {
            foreach (
                Transform child
                in parent.GetComponentsInChildren<Transform>(true)
            )
            {
                if (child.name == objectName)
                {
                    return child;
                }
            }


            return null;
        }


        // =========================================================
        // LOCK ALLIANCE PROCESSOR
        //
        // Whichever processor is closest to the robot at spawn
        // becomes its processor for the entire match.
        // =========================================================

        private void LockAllianceProcessor()
        {
            if (
                _scoringTriggerA == null
                ||
                _scoringTriggerB == null
            )
            {
                return;
            }


            Vector2 robotPosition =
                ToVector2(
                    transform.position
                );


            Vector2 processorAPosition =
                ToVector2(
                    _scoringTriggerA.position
                );


            Vector2 processorBPosition =
                ToVector2(
                    _scoringTriggerB.position
                );


            float distanceA =
                Vector2.Distance(
                    robotPosition,
                    processorAPosition
                );


            float distanceB =
                Vector2.Distance(
                    robotPosition,
                    processorBPosition
                );


            if (distanceA < distanceB)
            {
                _allianceProcessor =
                    _processorA;

                _allianceScoringTrigger =
                    _scoringTriggerA;


                Debug.Log(
                    "6995 Processor AutoAlign: Processor A locked."
                );
            }
            else
            {
                _allianceProcessor =
                    _processorB;

                _allianceScoringTrigger =
                    _scoringTriggerB;


                Debug.Log(
                    "6995 Processor AutoAlign: Processor B locked."
                );
            }
        }


        private void FixedUpdate()
        {
            if (
                _base == null
                ||
                _driveController == null
                ||
                _allianceScoringTrigger == null
            )
            {
                return;
            }


            // =====================================================
            // PROCESSOR STATE
            // =====================================================

            bool directlyAtProcessor =
                _base.CurrentSetpoint ==
                ReefscapeSetpoints.Processor;


            if (directlyAtProcessor)
            {
                _processorPlaceContext = true;
            }
            else if (
                _base.CurrentSetpoint !=
                ReefscapeSetpoints.Place
            )
            {
                _processorPlaceContext = false;
            }


            bool processorState =
                directlyAtProcessor
                ||
                (
                    _base.CurrentSetpoint ==
                    ReefscapeSetpoints.Place
                    &&
                    _processorPlaceContext
                );


            if (!processorState)
            {
                ResetAlignment();

                return;
            }


            // =====================================================
            // AUTO ALIGN BUTTON
            // =====================================================

            bool leftPressed =
                _base.AutoAlignLeftAction != null
                &&
                _base.AutoAlignLeftAction.IsPressed();


            bool rightPressed =
                _base.AutoAlignRightAction != null
                &&
                _base.AutoAlignRightAction.IsPressed();


            if (!leftPressed && !rightPressed)
            {
                ResetAlignment();

                return;
            }


            Vector2 currentPosition =
                ToVector2(
                    transform.position
                );


            if (!_targetLocked)
            {
                LockTarget(
                    currentPosition
                );
            }


            if (_targetLocked)
            {
                UpdateAlignment(
                    currentPosition
                );
            }
        }


        // =========================================================
        // LOCK TARGET
        // =========================================================

        private void LockTarget(
            Vector2 currentPosition
        )
        {
            CalculateStraightProcessorTarget();


            _lastPosition =
                currentPosition;


            _lastRotation =
                GetCurrentRobotAngle();


            _targetLocked = true;
            _isLockedOn = false;
        }


        // =========================================================
        // CALCULATE PROCESSOR TARGET
        //
        // IMPORTANT:
        //
        // The final position is centered directly on the
        // ScoringTrigger.
        //
        // The robot DOES NOT preserve its current sideways
        // position anymore.
        //
        // This makes Nomad move over and line up directly with
        // the processor opening before scoring.
        // =========================================================

        private void CalculateStraightProcessorTarget()
        {
            Vector2 triggerPosition =
                ToVector2(
                    _allianceScoringTrigger.position
                );


            Vector2 robotPosition =
                ToVector2(
                    transform.position
                );


            // =====================================================
            // PROCESSOR FORWARD DIRECTION
            // =====================================================

            Vector3 triggerForward3D =
                _allianceScoringTrigger.forward;


            Vector2 processorForward =
                new Vector2(
                    triggerForward3D.x,
                    triggerForward3D.z
                );


            if (
                processorForward.sqrMagnitude <
                0.001f
            )
            {
                Debug.LogError(
                    "6995 Processor AutoAlign: Invalid ScoringTrigger forward direction."
                );

                return;
            }


            processorForward.Normalize();


            // =====================================================
            // MAKE FORWARD POINT OUT OF PROCESSOR
            // =====================================================

            Vector2 triggerToRobot =
                robotPosition -
                triggerPosition;


            if (
                Vector2.Dot(
                    processorForward,
                    triggerToRobot
                ) < 0f
            )
            {
                processorForward =
                    -processorForward;
            }


            if (flipApproachDirection)
            {
                processorForward =
                    -processorForward;
            }


            // =====================================================
            // SIDEWAYS DIRECTION
            // =====================================================

            Vector2 processorSideways =
                new Vector2(
                    -processorForward.y,
                    processorForward.x
                );


            // =====================================================
            // FINAL TARGET POSITION
            //
            // ALWAYS centered directly with the ScoringTrigger.
            //
            // sidewaysOffset can be used for small manual
            // corrections if the trigger is not visually centered.
            // =====================================================

            _targetPosition =
                triggerPosition
                +
                processorSideways
                *
                sidewaysOffset
                +
                processorForward
                *
                processorDistance;


            // =====================================================
            // TARGET ROTATION
            //
            // Front of robot points directly into processor.
            // =====================================================

            Vector2 directionTowardProcessor =
                -processorForward;


            float worldAngle =
                Mathf.Atan2(
                    directionTowardProcessor.y,
                    directionTowardProcessor.x
                )
                *
                Mathf.Rad2Deg;


            _targetRotation =
                -worldAngle
                +
                90f
                +
                rotationOffset;
        }


        // =========================================================
        // UPDATE ALIGNMENT
        // =========================================================

        private void UpdateAlignment(
            Vector2 currentPosition
        )
        {
            Vector2 positionError =
                _targetPosition -
                currentPosition;


            _currentTranslationError =
                positionError.magnitude;


            float currentRotation =
                GetCurrentRobotAngle();


            float rotationError =
                Mathf.DeltaAngle(
                    currentRotation,
                    _targetRotation
                );


            _currentRotationError =
                Mathf.Abs(
                    rotationError
                );


            // =====================================================
            // TRANSLATION VELOCITY
            // =====================================================

            Vector2 velocity =
                (
                    currentPosition -
                    _lastPosition
                )
                /
                Time.fixedDeltaTime;


            _lastPosition =
                currentPosition;


            // =====================================================
            // ROTATION VELOCITY
            // =====================================================

            float angularVelocity =
                Mathf.DeltaAngle(
                    _lastRotation,
                    currentRotation
                )
                /
                Time.fixedDeltaTime;


            _lastRotation =
                currentRotation;


            // =====================================================
            // TOLERANCES
            // =====================================================

            bool translationPerfect =
                _currentTranslationError <
                translationTolerance;


            bool rotationPerfect =
                _currentRotationError <
                rotationTolerance;


            // =====================================================
            // HOLD LOCK
            // =====================================================

            if (_isLockedOn)
            {
                bool translationDrifted =
                    _currentTranslationError >
                    translationTolerance +
                    0.03f;


                bool rotationDrifted =
                    _currentRotationError >
                    rotationTolerance +
                    0.5f;


                if (
                    translationDrifted
                    ||
                    rotationDrifted
                )
                {
                    _isLockedOn = false;
                }
                else
                {
                    StopDrivetrain();

                    return;
                }
            }


            // =====================================================
            // ALIGNED
            // =====================================================

            if (
                translationPerfect
                &&
                rotationPerfect
            )
            {
                _isLockedOn = true;

                StopDrivetrain();

                return;
            }


            // =====================================================
            // DRIVE
            // =====================================================

            Vector2 translationCommand =
                CalculateTranslationCommand(
                    positionError,
                    velocity,
                    translationPerfect
                );


            float rotationCommand =
                CalculateRotationCommand(
                    rotationError,
                    angularVelocity,
                    rotationPerfect
                );


            _driveController.overideInput(
                translationCommand,
                rotationCommand,
                (DriveController.DriveMode)0
            );
        }


        // =========================================================
        // TRANSLATION PD
        // =========================================================

        private Vector2 CalculateTranslationCommand(
            Vector2 error,
            Vector2 velocity,
            bool perfect
        )
        {
            if (perfect)
            {
                return Vector2.zero;
            }


            Vector2 command =
                error *
                translationKp
                -
                velocity *
                translationKd;


            if (command.magnitude > 1f)
            {
                command.Normalize();
            }
            else if (
                command.magnitude >
                0.0001f
                &&
                command.magnitude <
                minPower
            )
            {
                command =
                    command.normalized *
                    minPower;
            }


            return command;
        }


        // =========================================================
        // ROTATION PD
        // =========================================================

        private float CalculateRotationCommand(
            float errorDegrees,
            float angularVelocityDegrees,
            bool perfect
        )
        {
            if (perfect)
            {
                return 0f;
            }


            float proportional =
                errorDegrees *
                Mathf.Deg2Rad *
                rotateKp;


            float derivative =
                angularVelocityDegrees *
                Mathf.Deg2Rad *
                rotateKd;


            float command =
                proportional -
                derivative;


            if (
                Mathf.Abs(command) >
                0.0001f
                &&
                Mathf.Abs(command) <
                minPower
            )
            {
                command =
                    Mathf.Sign(command) *
                    minPower;
            }


            return Mathf.Clamp(
                command,
                -1f,
                1f
            );
        }


        // =========================================================
        // ROBOT ANGLE
        // =========================================================

        private float GetCurrentRobotAngle()
        {
            return
                -1f *
                (
                    transform.eulerAngles.y -
                    90f
                );
        }


        // =========================================================
        // STOP
        // =========================================================

        private void StopDrivetrain()
        {
            if (_driveController == null)
            {
                return;
            }


            _driveController.overideInput(
                Vector2.zero,
                0f,
                (DriveController.DriveMode)0
            );
        }


        // =========================================================
        // RESET
        // =========================================================

        private void ResetAlignment()
        {
            _targetLocked = false;
            _isLockedOn = false;

            _currentTranslationError =
                float.MaxValue;

            _currentRotationError =
                float.MaxValue;
        }


        // =========================================================
        // VECTOR CONVERSION
        // =========================================================

        private Vector2 ToVector2(
            Vector3 value
        )
        {
            return new Vector2(
                value.x,
                value.z
            );
        }


        // =========================================================
        // DEBUG
        // =========================================================

        public float GetTranslationError()
        {
            return _currentTranslationError;
        }


        public float GetRotationError()
        {
            return _currentRotationError;
        }
    }
}