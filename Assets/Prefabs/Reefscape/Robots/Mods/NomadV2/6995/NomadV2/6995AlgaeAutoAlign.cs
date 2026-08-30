using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using RobotFramework.Controllers.Drivetrain;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    public class NomadV2AutoAlign : MonoBehaviour
    {
        [HideInInspector] public Vector3 offset;

        [Header("Algae De-score Alignment")]
        public float reefDistance = 1.3f;
        public float algaeExtraDistanceInches = 5f;
        public float rotationOffset = 0f;

        [Header("Back Away After Algae Capture")]
        [SerializeField] private float algaeBackAwayDistance = 0.35f;
        [Tooltip("How long the robot waits after the algae is detected before backing away.")]
        [SerializeField] private float algaeCaptureWaitTime = 0.35f;

        [Header("Drive Tuning (PD Loop)")]
        public float translationKp = 3.5f;
        public float translationKd = 0.2f;
        public float rotateKp = 1.5f;
        public float rotateKd = 0.1f;
        public float minPower = 0.08f;

        [Header("Tolerances")]
        public float translationTolerance = 0.05f;
        public float rotationTolerance = 1.5f;

        private ReefscapeRobotBase _base;
        private NomadV2 _nomad;
        private DriveController _driveController;

        [Header("Algae Intake Detection")]
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;

        private Vector2 _blueReef;
        private Vector2 _redReef;
        private float _currentTranslationError;
        private float _currentRotationError;
        private bool _isLockedOn;
        private bool _targetLocked;
        private Vector2 _lockedReefCenter;
        private float _lockedFaceAngle;
        private bool _lockedUseFront;
        private Vector2 _lastPos2D;
        private float _lastAngleDeg;
        private bool _algaePlaceContext;
        private bool _hadAlgaeWhenAlignStarted;
        private bool _backingAwayAfterCapture;
        private bool _waitingAfterCapture;
        private float _captureWaitTimer;

        private const float INCHES_TO_METERS = 0.0254f;
        private const float SIXTH_PI = Mathf.PI / 6f;
        private const float THIRD_PI = Mathf.PI / 3f;

        private void Start()
        {
            _base = GetComponent<ReefscapeRobotBase>();
            _nomad = GetComponent<NomadV2>();
            _driveController = GetComponent<DriveController>();

            GameObject blueReefObject = GameObject.Find("BlueReef");
            GameObject redReefObject = GameObject.Find("RedReef");

            if (blueReefObject != null) _blueReef = Vec3ToVec2(blueReefObject.transform.position);
            else Debug.LogError("6995 Algae AutoAlign could not find BlueReef.");

            if (redReefObject != null) _redReef = Vec3ToVec2(redReefObject.transform.position);
            else Debug.LogError("6995 Algae AutoAlign could not find RedReef.");

            _currentTranslationError = float.MaxValue;
            _currentRotationError = float.MaxValue;
        }

        private void FixedUpdate()
        {
            if (_base == null || _driveController == null) return;

            bool directlyAtAlgae =
                _base.CurrentSetpoint == ReefscapeSetpoints.LowAlgae ||
                _base.CurrentSetpoint == ReefscapeSetpoints.HighAlgae;

            if (directlyAtAlgae) _algaePlaceContext = true;
            else if (_base.CurrentSetpoint != ReefscapeSetpoints.Place) _algaePlaceContext = false;

            bool algaeDescoreState = directlyAtAlgae ||
                (_base.CurrentSetpoint == ReefscapeSetpoints.Place && _algaePlaceContext);

            if (!algaeDescoreState) { ResetAlignment(); return; }

            bool leftPressed = _base.AutoAlignLeftAction != null && _base.AutoAlignLeftAction.IsPressed();
            bool rightPressed = _base.AutoAlignRightAction != null && _base.AutoAlignRightAction.IsPressed();
            if (!leftPressed && !rightPressed) { ResetAlignment(); return; }

            Vector2 currentPos2D = Vec3ToVec2(transform.position);
            if (!_targetLocked) LockAlignmentTarget(currentPos2D);

            if (!_hadAlgaeWhenAlignStarted && !_waitingAfterCapture && !_backingAwayAfterCapture &&
                algaeIntake != null && algaeIntake.GamePiece != null)
            {
                _waitingAfterCapture = true;
                _captureWaitTimer = 0f;
                _isLockedOn = true;
            }

            if (_waitingAfterCapture)
            {
                // Hold the robot at the reef briefly so the algae has time to finish intaking.
                StopDrivetrain();
                _captureWaitTimer += Time.fixedDeltaTime;

                if (_captureWaitTimer >= algaeCaptureWaitTime)
                {
                    _waitingAfterCapture = false;
                    _backingAwayAfterCapture = true;
                    _isLockedOn = false;
                    _lastPos2D = currentPos2D;
                    _lastAngleDeg = GetCurrentRobotAngle();
                }
                else
                {
                    return;
                }
            }

            UpdateAlignment(currentPos2D);
        }

        private void LockAlignmentTarget(Vector2 currentPos2D)
        {
            float blueDistance = Vector2.Distance(currentPos2D, _blueReef);
            float redDistance = Vector2.Distance(currentPos2D, _redReef);
            _lockedReefCenter = blueDistance < redDistance ? _blueReef : _redReef;

            Vector2 reefToRobot = currentPos2D - _lockedReefCenter;
            if (reefToRobot.sqrMagnitude < 0.001f) reefToRobot = Vector2.right;

            float rawAngle = Mathf.Atan2(reefToRobot.y, reefToRobot.x);
            _lockedFaceAngle = Mathf.Floor((rawAngle + SIXTH_PI) / THIRD_PI) * THIRD_PI;

            // Use the exact same front/back decision as the main NomadV2 code.
            // Lock it once when auto-align starts so the robot cannot switch
            // between front and back while it is already aligning.
            _lockedUseFront = _nomad == null || _nomad.GetFacingReefForAlign();

            _targetLocked = true;
            _isLockedOn = false;
            _lastPos2D = currentPos2D;
            _lastAngleDeg = GetCurrentRobotAngle();
            _hadAlgaeWhenAlignStarted = algaeIntake != null && algaeIntake.GamePiece != null;
            _backingAwayAfterCapture = false;
            _waitingAfterCapture = false;
            _captureWaitTimer = 0f;
        }

        private void UpdateAlignment(Vector2 currentPos2D)
        {
            Vector2 targetPos2D = GetTargetPosition();
            Vector2 error2D = targetPos2D - currentPos2D;
            _currentTranslationError = error2D.magnitude;

            float currentAngleDeg = GetCurrentRobotAngle();
            float rotationErrorDeg = Mathf.DeltaAngle(currentAngleDeg, GetTargetRobotAngle());
            _currentRotationError = Mathf.Abs(rotationErrorDeg);

            Vector2 velocity = (currentPos2D - _lastPos2D) / Time.fixedDeltaTime;
            _lastPos2D = currentPos2D;

            float angularVelocity = Mathf.DeltaAngle(_lastAngleDeg, currentAngleDeg) / Time.fixedDeltaTime;
            _lastAngleDeg = currentAngleDeg;

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

            Vector2 translationCommand = CalculateTranslationCommand(error2D, velocity, translationPerfect);
            float rotationCommand = CalculateRotationCommand(rotationErrorDeg, angularVelocity, rotationPerfect);
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

        private float CalculateRotationCommand(float error, float angularVelocity, bool perfect)
        {
            if (perfect) return 0f;
            float command = error * Mathf.Deg2Rad * rotateKp
                          - angularVelocity * Mathf.Deg2Rad * rotateKd;
            if (Mathf.Abs(command) > 0.0001f && Mathf.Abs(command) < minPower)
                command = Mathf.Sign(command) * minPower;
            return Mathf.Clamp(command, -1f, 1f);
        }

        private Vector2 GetTargetPosition()
        {
            float distance = reefDistance + algaeExtraDistanceInches * INCHES_TO_METERS;
            if (_backingAwayAfterCapture) distance += algaeBackAwayDistance;
            Vector2 outwardDirection = Rotate2(Vector2.right, _lockedFaceAngle);
            return _lockedReefCenter + outwardDirection * distance;
        }

        private float GetTargetRobotAngle()
        {
            float faceAngleDeg = _lockedFaceAngle * Mathf.Rad2Deg;

            // This is the original working FRONT formula.
            float targetAngle = faceAngleDeg + 180f + rotationOffset;

            // If the main robot says the back should face the reef,
            // rotate the robot exactly 180 degrees while keeping the
            // same physical algae target position on the reef face.
            if (!_lockedUseFront)
                targetAngle += 180f;

            return targetAngle;
        }

        private float GetCurrentRobotAngle() => -1f * (transform.eulerAngles.y - 90f);

        private void StopDrivetrain()
        {
            _driveController.overideInput(Vector2.zero, 0f, (DriveController.DriveMode)0);
        }

        private void ResetAlignment()
        {
            _isLockedOn = false;
            _targetLocked = false;
            _hadAlgaeWhenAlignStarted = false;
            _backingAwayAfterCapture = false;
            _waitingAfterCapture = false;
            _captureWaitTimer = 0f;
            _currentTranslationError = float.MaxValue;
            _currentRotationError = float.MaxValue;
        }

        private Vector2 Rotate2(Vector2 vector, float radians)
        {
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
        }

        private Vector2 Vec3ToVec2(Vector3 value) => new Vector2(value.x, value.z);
        public float GetTranslationError() => _currentTranslationError;
        public float GetRotationError() => _currentRotationError;
    }
}
