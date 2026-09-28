// GamePieceController.cs - State holder with minimal logic
using System;
using System.Collections;
using MoSimLib;
using RobotFramework.GamePieceSystem;
using RobotFramework.Interfaces;
using UnityEngine;

namespace RobotFramework.Controllers.GamePieceSystem
{
    public abstract class GamePieceController<TPiece, TData> : MonoBehaviour
        where TPiece : GamePiece<TData>
        where TData : IGamePieceData
    {
        [SerializeField] protected TData gamePieceData;
        protected TPiece gamePiece;

        public TPiece GamePiece => gamePiece;
        public string PieceName => gamePieceData.Name;
        public bool IsScored { get; set; } = false;
        public bool debugLog;

        // State
        public Vector3 Distance { get; private set; }
        public float DistanceMagnitude { get; private set; }

        private float _startingMass;
        private float _startingDrag;
        private float _startingAngularDrag;
        private bool _moving;
        private bool _isControlled;
        private int _lastBreakableResult;

        private Transform _gamePieceWorld;
        private Collider[] _colliders;
        private Transform _colliderParent;
        private Vector3 _startPosition;
        private Quaternion _startRotation;

        private void Awake()
        {
            gamePiece = Activator.CreateInstance<TPiece>();
            gamePiece.Initialize(gamePieceData, gameObject);
        }

        private void Start()
        {
            _moving = false;
            _isControlled = false;
            _colliderParent = Utils.FindChild("Colliders", gameObject).transform;
            _startPosition = _colliderParent.localPosition;
            _startRotation = _colliderParent.localRotation;
            _colliders = new Collider[_colliderParent.transform.childCount];
            
            for (var i = 0; i < _colliderParent.transform.childCount; i++)
            {
                _colliders[i] = _colliderParent.transform.GetChild(i).GetComponent<Collider>();
            }

            if (!GamePiece.rigidbody)
            {
                GamePiece.rigidbody = gameObject.GetComponent<Rigidbody>();
                _startingMass = GamePiece.rigidbody.mass;
                _startingDrag = GamePiece.rigidbody.drag;
                _startingAngularDrag = GamePiece.rigidbody.angularDrag;
            }

            if (GamePiece.rigidbody)
            {
                GamePiece.rigidbody.automaticInertiaTensor = false;
                GamePiece.rigidbody.inertiaTensor = new Vector3(0.01f, 0.01f, 0f);
                GamePiece.rigidbody.inertiaTensorRotation = Quaternion.identity;
                GamePiece.rigidbody.ResetInertiaTensor();
            }
            else
            {
                Debug.LogError("GamePieceController: Rigidbody is null on " + gameObject.name);
            }
        }

        private void LateUpdate()
        {
            if (!_gamePieceWorld)
            {
                _gamePieceWorld = GameObject.FindGameObjectWithTag("GamePieceWorld").transform;
            }

            if (!GamePiece.rigidbody)
            {
                GamePiece.rigidbody = gameObject.GetComponent<Rigidbody>();
                _startingMass = GamePiece.rigidbody.mass;
                _startingDrag = GamePiece.rigidbody.drag;
                _startingAngularDrag = GamePiece.rigidbody.angularDrag;
            }

            if (!GamePiece.transform)
                GamePiece.transform = gameObject.transform;

            if (!GamePiece.owner)
                GamePiece.owner = transform.parent.gameObject;

            if (!GamePiece.gameObject)
                GamePiece.gameObject = gameObject;

            if (GamePiece.owner.layer == LayerMask.NameToLayer("Robot") && !_moving)
            {
                Controlled();
            }
        }

        private void OnDestroy()
        {
            PieceController.ClearState(gameObject.GetInstanceID());
        }

        public int MoveBreakable(Transform target, float intakeSpeed, float intakeForce, float maxDistance,
            float accuracy, float rotationForce, float maxRotationSpeed, bool useRotation, bool smoothHandoff,
            bool planarTolerance, Vector3 boxExtents, bool lockXAxis, bool lockYAxis, bool lockZAxis)
        {
            if (!_gamePieceWorld) return 0;
            _moving = true;
            var result = PieceController.MoveToBreakable(
                GamePiece, target, intakeSpeed, intakeForce, maxDistance, accuracy,
                rotationForce, maxRotationSpeed, useRotation, planarTolerance, boxExtents,
                lockXAxis, lockYAxis, lockZAxis, out var distance, out var distanceMagnitude);

            Distance = distance;
            DistanceMagnitude = distanceMagnitude;

            if (result != _lastBreakableResult)
            {
                Log($"MoveBreakable {_lastBreakableResult} -> {result} target={target.name} dist={distanceMagnitude / 0.0254f:F2}in " +
                    $"vel={GamePiece.rigidbody.velocity.magnitude:F2}m/s angVel={GamePiece.rigidbody.angularVelocity.magnitude:F2}rad/s");
                _lastBreakableResult = result;
            }

            GamePiece.owner = Mathf.Approximately(result, 1) ? target.gameObject : GamePiece.owner;
            if (result == 1)
            {
                _moving = smoothHandoff;
                Controlled(smoothHandoff);
            }

            return result;
        }

        public int Move(Transform target, float moveSpeed, float angularSpeed, bool smoothHandoff)
        {
            if (!_gamePieceWorld) return 0;
            _moving = true;
            var result = PieceController.MoveTo(
                GamePiece, gamePieceData, target, moveSpeed, angularSpeed, out var distance, out var distanceMagnitude);

            Distance = distance;
            DistanceMagnitude = distanceMagnitude;

            if (result == 1)
            {
                Log($"reached state target {target.name}");
                _moving = smoothHandoff;
                SetParent(target);
            }
            return result;
        }

        public void Release(Vector3 force, ForceMode forceMode = ForceMode.Impulse)
        {
            if (!_gamePieceWorld) return;
            Log($"released force={force}");
            UseRobot();
            SetParent(_gamePieceWorld);
            _colliderParent.parent = gameObject.transform;
            _colliderParent.localPosition = Vector3.zero + _startPosition;
            _colliderParent.localRotation = Quaternion.identity * _startRotation;
            GamePiece.rigidbody.velocity = Vector3.zero;
            GamePiece.rigidbody.angularVelocity = Vector3.zero;
            GamePiece.rigidbody.AddForce(transform.TransformDirection(force), forceMode);
        }

        public IEnumerator ContinuedRelease(Vector3 force, float time, float maxSpeed, ForceMode forceMode = ForceMode.Impulse)
        {
            if (!_gamePieceWorld) yield return null;
            Log($"released (continued) force={force} time={time}");
            UseRobot();
            SetParent(_gamePieceWorld);
            var startTime = Time.time;
            _colliderParent.parent = gameObject.transform;
            _colliderParent.localPosition = Vector3.zero + _startPosition;
            _colliderParent.localRotation = Quaternion.identity * _startRotation;
            GamePiece.rigidbody.velocity = Vector3.zero;
            GamePiece.rigidbody.angularVelocity = Vector3.zero;
            
            while (Time.time - startTime < time)
            {
                if (GamePiece.rigidbody.velocity.magnitude < maxSpeed)
                {
                    GamePiece.rigidbody.AddForce(transform.TransformDirection(force), forceMode);
                }
                yield return null;
            }
        }

        private void SetParent(Transform parent)
        {
            if (!_gamePieceWorld) return;
            GamePiece.owner = parent.gameObject;
            GamePiece.transform.parent = parent;
            _colliderParent.parent = parent;
        }

        private void Controlled(bool smoothHandoff = false)
        {
            if (!_gamePieceWorld) return;
            GamePiece.transform.parent = GamePiece.owner.transform;
            if (!_isControlled)
            {
                Log($"controlled by {GamePiece.owner.name} smoothHandoff={smoothHandoff}");
                IgnoreRobot();
            }

            if (!smoothHandoff)
            {
                _colliderParent.localPosition = Vector3.zero + _startPosition;
                _colliderParent.localRotation = Quaternion.identity * _startRotation;
                GamePiece.rigidbody.rotation = GamePiece.transform.rotation;
                GamePiece.rigidbody.position = GamePiece.transform.position;
                GamePiece.transform.localPosition = Vector3.zero;
                GamePiece.transform.localRotation = Quaternion.identity;
            }
            GamePiece.rigidbody.useGravity = false;
            GamePiece.rigidbody.velocity = Vector3.zero;
            GamePiece.rigidbody.angularVelocity = Vector3.zero;
        }

        private void IgnoreRobot()
        {
            if (!_gamePieceWorld) return;
            GamePiece.rigidbody.isKinematic = false;
            GamePiece.rigidbody.automaticInertiaTensor = false;
            GamePiece.rigidbody.inertiaTensor = Vector3.zero;
            GamePiece.rigidbody.automaticCenterOfMass = false;
            GamePiece.rigidbody.detectCollisions = false;
            GamePiece.rigidbody.centerOfMass = transform.InverseTransformPoint(GamePiece.owner.transform.parent.position);
            GamePiece.rigidbody.interpolation = RigidbodyInterpolation.None;
            GamePiece.rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            
            var parent = Utils.FindParentObjectComponent<Rigidbody>(gameObject);
            parent.excludeLayers = (1 << gameObject.layer) | (1 << LayerMask.NameToLayer("Robot"));
            GamePiece.rigidbody.excludeLayers = LayerMask.GetMask("Robot");
            _colliderParent.parent = transform.parent.transform;
            
            for (var i = 0; i < _colliders.Length; i++)
            {
                _colliders[i].excludeLayers = LayerMask.GetMask("Robot");
            }

            _isControlled = true;
        }

        private void UseRobot()
        {
            if (!_gamePieceWorld) return;
            Utils.FindParentObjectComponent<Rigidbody>(gameObject).excludeLayers = LayerMask.GetMask("Robot");

            GamePiece.rigidbody.automaticInertiaTensor = false;
            GamePiece.rigidbody.inertiaTensor = new Vector3(0.01f, 0.01f, 0f);
            GamePiece.rigidbody.inertiaTensorRotation = Quaternion.identity;
            GamePiece.rigidbody.ResetInertiaTensor();

            GamePiece.rigidbody.automaticCenterOfMass = true;
            GamePiece.rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            GamePiece.rigidbody.collisionDetectionMode = CollisionDetectionMode.Continuous;
            GamePiece.rigidbody.isKinematic = false;
            GamePiece.rigidbody.useGravity = true;
            GamePiece.rigidbody.angularDrag = _startingAngularDrag;
            GamePiece.rigidbody.mass = _startingMass;
            GamePiece.rigidbody.drag = _startingDrag;
            GamePiece.rigidbody.excludeLayers = LayerMask.GetMask();
            GamePiece.rigidbody.detectCollisions = true;
            _colliderParent.parent = GamePiece.transform;
            _colliderParent.position = GamePiece.transform.position;
            _colliderParent.rotation = GamePiece.transform.rotation;
            
            for (var i = 0; i < _colliders.Length; i++)
            {
                _colliders[i].excludeLayers = LayerMask.GetMask();
            }

            _isControlled = false;
        }

        private void Log(string message)
        {
            if (debugLog) Debug.Log($"[Piece {name}#{gameObject.GetInstanceID()}] {Time.fixedTime:F3} {message}");
        }
    }
}