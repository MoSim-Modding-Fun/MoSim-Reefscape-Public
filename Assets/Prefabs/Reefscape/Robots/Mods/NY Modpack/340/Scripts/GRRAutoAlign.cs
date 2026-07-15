using Games.Reefscape.Robots;
using RobotFramework.Controllers.Drivetrain;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.GRR._340
{
    public class GRRAutoAlign : MonoBehaviour
    {

        [Header("Offsets")]
        [Tooltip("The offset in the X direction from the reef's center for the robot to target (Always positive)")]
        public float x = 1.3f;
        [Tooltip("The offset in the Y direction from the reef's center for the robot to target (Always positive)")]
        public float y = 0.17f;

        [Header("Wildlife Protection Program")]
        [Tooltip("Additional distance to add to the X offset for avoiding the reef while the robot's superstructure is preparing to score")]
        public float avoidDistance = 0.3f;
        [Tooltip("The amount of time in seconds to avoid the reef for while the robot's superstructure is preparing to score")]
        public float avoidTime = 0.5f;

        [Header("Translation")]
        [Tooltip("The configured maximum velocity of the robot, in ft/s")]
        public float maxVelocity = 17.0f;
        [Tooltip("The maximum deceleration of the auto-align controller, in ft/s/s")]
        public float maxDeceleration = 32.0f;
        [Tooltip("Arbitrary strength force for centering the robot on the targeted reef pole")]
        public float strength = 600f;
        [Tooltip("The tolerance at which the controller signals that the robot is in position")]
        public float positionTolerance = 0.05f;

        [Header("Rotation")]
        [Tooltip("Kp constant to apply to the robot's rotation")]
        public float rotateKp = 1f;

        private ReefscapeRobotBase _base;
        private DriveController _driveController;
        private bool _usePerspective;
        private Vector2 _blueReef;
        private Vector2 _redReef;

        private bool _active = false;
        private bool _left = true;
        private bool _inPosition = false;
        private float _reefDistance = 0f;
        private bool _fallingEdge = false;
        private float _waitUntil = 0f;

        protected void Start()
        {
            _base = gameObject.GetComponent<ReefscapeRobotBase>();
            _driveController = gameObject.GetComponent<DriveController>();
            _usePerspective = PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1;
            _blueReef = Vec3ToVec2(GameObject.Find("BlueReef").transform.position);
            _redReef = Vec3ToVec2(GameObject.Find("RedReef").transform.position);
        }

        protected void FixedUpdate()
        {
            var max_v = maxVelocity * FT_TO_M;
            var max_a = maxDeceleration * FT_TO_M;

            var robot = Vec3ToVec2(_base.transform.position);
            bool c_l = _base.AutoAlignLeftAction.IsPressed();
            bool pressed = c_l || _base.AutoAlignRightAction.IsPressed();
            bool cm_f = _base.GetActiveCamera().transform.forward.x < 0;

            float blueDistance = (_blueReef - robot).magnitude;
            float redDistance = (_redReef - robot).magnitude;
            var reef = blueDistance < redDistance ? _blueReef : _redReef;
            _reefDistance = Mathf.Min(blueDistance, redDistance);

            var face = Rotate2((reef - robot).normalized, SIXTH_PI);
            float rk_w = Mathf.Floor(Mathf.Atan2(face.y, face.x) / THIRD_PI) * THIRD_PI;
            _left = !_usePerspective ? c_l : cm_f ^ (Mathf.Abs(rk_w) < HALF_PI) ^ !c_l; 

            bool waiting = Time.time < _waitUntil;
            float rk_x = x + (waiting ? avoidDistance : 0f);
            var pole = Pole(rk_x, rk_w, _left, reef);
            var error = pole - robot;

            _inPosition = error.magnitude < positionTolerance && !waiting;
            _fallingEdge = pressed && (_fallingEdge || _inPosition);

            if (!_fallingEdge && pressed)
            {
                float k = 1f;
                if (error.magnitude < max_v * max_v / (2f * max_a))
                {
                    k = Mathf.Sqrt(2f * max_a * error.magnitude) / max_v;
                }

                var force = error.normalized;

                var att = Pole(SL, rk_w, _left, reef) - pole;
                var proj = Rotate2(robot - pole, -Mathf.Atan2(att.y, att.x));
                if (proj.x > 0.0 && proj.x < SL - rk_x)
                {
                    float t = proj.y / SW / 2f;
                    float m = -strength * t * t * Mathf.Sign(proj.y) / Mathf.Abs(proj.x * 4f);
                    force += m * Rotate2(Vector2.down, rk_w);
                }

                float yaw = -Mathf.Deg2Rad * (_base.transform.rotation.eulerAngles.y - 90f);
                float alpha = rotateKp * ((rk_w - yaw + THREE_PI) % TWO_PI - Mathf.PI);

                _driveController.overideInput(force.normalized * k, alpha, DriveController.DriveMode.FieldOriented);
                _active = true;
            }
            else
            {
                _waitUntil = Time.time + avoidTime;
                _active = false;
            }
        }

        private Vector2 Pole(float x, float angle, bool left, Vector2 reef)
        {
            return reef + Rotate2(new Vector2(-x, y * (left ? 1f : -1f)), angle);
        }

        private Vector2 Rotate2(Vector2 v, float radians)
        {
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
        }

        private Vector2 Vec3ToVec2(Vector3 vec3)
        {
            return new Vector2(vec3.x, vec3.z);
        }

        public bool Active()
        {
            return _active;
        }

        public bool Left()
        {
            return _left;
        }

        public bool InPosition()
        {
            return _inPosition;
        }

        public float ReefDistance()
        {
            return _reefDistance;
        }

        private const float SL = 10f;
        private const float SW = 6f;
        private const float FT_TO_M = 0.3048f;
        private const float SIXTH_PI = Mathf.PI / 6f;
        private const float THIRD_PI = Mathf.PI / 3f;
        private const float HALF_PI = Mathf.PI / 2f;
        private const float TWO_PI = Mathf.PI * 2f;
        private const float THREE_PI = Mathf.PI * 3f;
    }
}