using Games.Reefscape.Enums;
using Games.Reefscape.Robots;
using RobotFramework.Controllers.Drivetrain;
using System.Collections;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    public class NomadV2BargeAutoAlign : MonoBehaviour
    {
        [Header("Alliance Detection")]
        [Tooltip("Wait briefly so the robot has reached its real field spawn before locking alliance.")]
        [SerializeField] private float allianceLockDelay = 0.35f;

        [Header("Barge Alignment")]
        [SerializeField] private float bargeDistance = 1.0f;
        [SerializeField] private float sidewaysOffset = 0f;
        [SerializeField] private float rotationOffset = 0f;

        [Tooltip("How far the robot may slide along its alliance section of the barge. Leave at -1 to calculate it automatically from BlueNet/RedNet.")]
        [SerializeField] private float allianceSidewaysRange = -1f;

        [Tooltip("Keeps the automatic target slightly away from the center line between alliances.")]
        [SerializeField] private float centerLineSafetyMargin = 0.10f;

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
        private Transform _blueNet;
        private Transform _redNet;
        private Transform _allianceNet;
        private Vector2 _blueReef;
        private Vector2 _redReef;
        private bool _isBlueAlliance;
        private bool _allianceLocked;
        private bool _bargePlaceContext;
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

            GameObject blueNetObject = GameObject.Find("BlueNet");
            GameObject redNetObject = GameObject.Find("RedNet");
            if (blueNetObject != null) _blueNet = blueNetObject.transform;
            else Debug.LogError("6995 Barge AutoAlign: Could not find BlueNet.");

            if (redNetObject != null) _redNet = redNetObject.transform;
            else Debug.LogError("6995 Barge AutoAlign: Could not find RedNet.");

            GameObject blueReefObject = GameObject.Find("BlueReef");
            GameObject redReefObject = GameObject.Find("RedReef");

            if (blueReefObject != null) _blueReef = ToVector2(blueReefObject.transform.position);
            else Debug.LogError("6995 Barge AutoAlign: Could not find BlueReef.");

            if (redReefObject != null) _redReef = ToVector2(redReefObject.transform.position);
            else Debug.LogError("6995 Barge AutoAlign: Could not find RedReef.");

            // Do NOT lock alliance immediately in Start().
            // Some robot instances are moved to their real spawn position just
            // after Start(), which can make instant detection choose the wrong net.
            StartCoroutine(LockAllianceAfterSpawn());

            _currentTranslationError = float.MaxValue;
            _currentRotationError = float.MaxValue;
        }

        private IEnumerator LockAllianceAfterSpawn()
        {
            if (allianceLockDelay > 0f)
                yield return new WaitForSeconds(allianceLockDelay);

            LockAllianceAtSpawn();
        }

        private void LockAllianceAtSpawn()
        {
            if (_allianceLocked)
                return;

            if (_blueNet == null || _redNet == null)
                return;

            Vector2 spawnPosition = ToVector2(transform.position);
            float blueDistance = Vector2.Distance(spawnPosition, _blueReef);
            float redDistance = Vector2.Distance(spawnPosition, _redReef);

            _isBlueAlliance = blueDistance < redDistance;
            _allianceNet = _isBlueAlliance ? _blueNet : _redNet;
            _allianceLocked = true;

            Debug.Log(_isBlueAlliance
                ? "6995 Barge AutoAlign: BLUE alliance permanently locked to BlueNet."
                : "6995 Barge AutoAlign: RED alliance permanently locked to RedNet.");
        }

        private void FixedUpdate()
        {
            if (_base == null || _driveController == null || !_allianceLocked || _allianceNet == null) return;

            bool directlyAtBarge = _base.CurrentSetpoint == ReefscapeSetpoints.Barge;
            if (directlyAtBarge) _bargePlaceContext = true;
            else if (_base.CurrentSetpoint != ReefscapeSetpoints.Place) _bargePlaceContext = false;

            bool bargeState = directlyAtBarge ||
                              (_base.CurrentSetpoint == ReefscapeSetpoints.Place && _bargePlaceContext);

            if (!bargeState) { ResetAlignment(); return; }

            bool leftPressed = _base.AutoAlignLeftAction != null && _base.AutoAlignLeftAction.IsPressed();
            bool rightPressed = _base.AutoAlignRightAction != null && _base.AutoAlignRightAction.IsPressed();

            if (!leftPressed && !rightPressed) { ResetAlignment(); return; }

            Vector2 currentPosition = ToVector2(transform.position);
            if (!_targetLocked) LockTarget(currentPosition);
            UpdateAlignment(currentPosition);
        }

        private void LockTarget(Vector2 currentPosition)
        {
            CalculateStraightTarget();
            _lastPosition = currentPosition;
            _lastRotation = GetCurrentRobotAngle();
            _targetLocked = true;
            _isLockedOn = false;
        }

        private void CalculateStraightTarget()
        {
            Vector2 allianceNetPosition = ToVector2(_allianceNet.position);
            Vector2 blueNetPosition = ToVector2(_blueNet.position);
            Vector2 redNetPosition = ToVector2(_redNet.position);
            Vector2 robotPosition = ToVector2(transform.position);

            Vector2 netCenterDifference = redNetPosition - blueNetPosition;
            float netCenterDistance = netCenterDifference.magnitude;

            if (netCenterDistance < 0.001f)
            {
                Debug.LogError("6995 Barge AutoAlign: BlueNet and RedNet positions are too close.");
                return;
            }

            Vector2 bargeSidewaysDirection = netCenterDifference / netCenterDistance;
            Vector2 bargeForwardDirection =
                new Vector2(-bargeSidewaysDirection.y, bargeSidewaysDirection.x);

            Vector2 netToRobot = robotPosition - allianceNetPosition;

            // Approach the selected alliance barge from whichever side of the
            // barge the robot is currently on.
            if (Vector2.Dot(bargeForwardDirection, netToRobot) < 0f)
                bargeForwardDirection = -bargeForwardDirection;

            // Find the closest point ALONG this alliance's section of the barge.
            // This restores the old "align wherever I am closest" behavior,
            // but clamps it so the target can never wander into the other alliance's section.
            float robotSidewaysPosition =
                Vector2.Dot(robotPosition - allianceNetPosition, bargeSidewaysDirection);

            float maxSideways;

            if (allianceSidewaysRange > 0f)
            {
                maxSideways = allianceSidewaysRange;
            }
            else
            {
                // BlueNet and RedNet are treated as the centers of their two alliance sections.
                // Half the distance between them reaches the center boundary.
                maxSideways = Mathf.Max(0.05f, netCenterDistance * 0.5f - centerLineSafetyMargin);
            }

            float clampedSidewaysPosition =
                Mathf.Clamp(robotSidewaysPosition + sidewaysOffset, -maxSideways, maxSideways);

            _targetPosition =
                allianceNetPosition
                + bargeSidewaysDirection * clampedSidewaysPosition
                + bargeForwardDirection * bargeDistance;

            Vector2 directionTowardBarge = -bargeForwardDirection;

            float worldAngle =
                Mathf.Atan2(directionTowardBarge.y, directionTowardBarge.x)
                * Mathf.Rad2Deg;

            _targetRotation =
                -worldAngle + 90f + rotationOffset;
        }

        private void UpdateAlignment(Vector2 currentPosition)
        {
            Vector2 positionError = _targetPosition - currentPosition;
            _currentTranslationError = positionError.magnitude;

            float currentRotation = GetCurrentRobotAngle();
            float rotationError = Mathf.DeltaAngle(currentRotation, _targetRotation);
            _currentRotationError = Mathf.Abs(rotationError);

            Vector2 velocity = (currentPosition - _lastPosition) / Time.fixedDeltaTime;
            _lastPosition = currentPosition;

            float angularVelocity = Mathf.DeltaAngle(_lastRotation, currentRotation) / Time.fixedDeltaTime;
            _lastRotation = currentRotation;

            bool translationPerfect = _currentTranslationError < translationTolerance;
            bool rotationPerfect = _currentRotationError < rotationTolerance;

            if (_isLockedOn)
            {
                if (_currentTranslationError > translationTolerance + 0.03f ||
                    _currentRotationError > rotationTolerance + 0.5f)
                    _isLockedOn = false;
                else { StopDrivetrain(); return; }
            }

            if (translationPerfect && rotationPerfect)
            {
                _isLockedOn = true;
                StopDrivetrain();
                return;
            }

            Vector2 translationCommand = CalculateTranslationCommand(positionError, velocity, translationPerfect);
            float rotationCommand = CalculateRotationCommand(rotationError, angularVelocity, rotationPerfect);

            _driveController.overideInput(translationCommand, rotationCommand, (DriveController.DriveMode)0);
        }

        private Vector2 CalculateTranslationCommand(Vector2 error, Vector2 velocity, bool perfect)
        {
            if (perfect) return Vector2.zero;
            Vector2 command = error * translationKp - velocity * translationKd;
            if (command.magnitude > 1f) command.Normalize();
            else if (command.magnitude > 0.0001f && command.magnitude < minPower)
                command = command.normalized * minPower;
            return command;
        }

        private float CalculateRotationCommand(float errorDegrees, float angularVelocityDegrees, bool perfect)
        {
            if (perfect) return 0f;
            float command = errorDegrees * Mathf.Deg2Rad * rotateKp
                          - angularVelocityDegrees * Mathf.Deg2Rad * rotateKd;
            if (Mathf.Abs(command) > 0.0001f && Mathf.Abs(command) < minPower)
                command = Mathf.Sign(command) * minPower;
            return Mathf.Clamp(command, -1f, 1f);
        }

        private float GetCurrentRobotAngle() => -1f * (transform.eulerAngles.y - 90f);

        private void StopDrivetrain()
        {
            _driveController.overideInput(Vector2.zero, 0f, (DriveController.DriveMode)0);
        }

        private void ResetAlignment()
        {
            _targetLocked = false;
            _isLockedOn = false;
            _currentTranslationError = float.MaxValue;
            _currentRotationError = float.MaxValue;
        }

        private Vector2 ToVector2(Vector3 value) => new Vector2(value.x, value.z);

        public bool IsBlueAlliance() => _isBlueAlliance;
        public float GetTranslationError() => _currentTranslationError;
        public float GetRotationError() => _currentRotationError;
    }
}
