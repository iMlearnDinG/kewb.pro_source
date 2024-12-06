using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NetworkedPlayerController : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float sprintMultiplier = 2f;
    [SerializeField] private float jumpForce = 7f;
    [SerializeField] private LayerMask groundLayer;
    [SerializeField, Range(0f, 1f)] private float reconcileInterpolationFactor = 0.2f; // Configurable interpolation factor

    private PredictionRigidbody _predictionRigidbody;
    private Vector3 _inputDirection;
    private bool _isSprinting;
    private bool _isJumping;

    private const int StateCacheSize = 1024;
    private MoveData[] _inputCache = new MoveData[StateCacheSize];
    private ReconcileData[] _stateCache = new ReconcileData[StateCacheSize];
    private uint _currentTick;

    private void Awake()
    {
        _predictionRigidbody = new PredictionRigidbody();
        _predictionRigidbody.Initialize(GetComponent<Rigidbody>());
    }

    public override void OnStartNetwork()
    {
        if (base.Owner.IsLocalClient)
        {
            base.TimeManager.OnTick += TimeManager_OnTick;
        }
        if (base.IsServer)
        {
            base.TimeManager.OnPostTick += TimeManager_OnPostTick; // Ensure the server runs reconciliation.
        }
    }

    public override void OnStopNetwork()
    {
        if (base.Owner.IsLocalClient)
        {
            base.TimeManager.OnTick -= TimeManager_OnTick;
        }
        if (base.IsServer)
        {
            base.TimeManager.OnPostTick -= TimeManager_OnPostTick;
        }
    }

    private void Update()
    {
        if (base.Owner.IsLocalClient)
        {
            HandleInput();
        }
    }

    private void Start()
    {
        if (base.Owner.IsLocalClient)
        {
            StartCoroutine(PrintRTT());
        }
    }

    private IEnumerator PrintRTT()
    {
        while (true)
        {
            yield return new WaitForSeconds(3f);
            long rtt = base.TimeManager.RoundTripTime;
            Debug.Log($"RTT: {rtt} ms");
        }
    }

    private void HandleInput()
    {
        Vector3 newInputDirection = new Vector3(Input.GetAxisRaw("Horizontal"), 0, Input.GetAxisRaw("Vertical")).normalized;
        _isSprinting = Input.GetKey(KeyCode.LeftShift);
        _isJumping = Input.GetKeyDown(KeyCode.Space);
        _inputDirection = newInputDirection;
    }

    private void TimeManager_OnTick()
    {
        if (base.IsOwner && (_inputDirection != Vector3.zero || _isJumping))
        {
            BuildMoveData(out MoveData moveData);
            int cacheIndex = (int)(base.TimeManager.LocalTick % StateCacheSize);
            _inputCache[cacheIndex] = moveData; // Cache inputs for replay

            SendInputToServer(moveData);
            Move(moveData, ReplicateState.CurrentCreated);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SendInputToServer(MoveData moveData)
    {
        if (ValidateInput(moveData))
        {
            Move(moveData, ReplicateState.CurrentCreated);
        }
    }

    private bool ValidateInput(MoveData moveData)
    {
        // Example input validation logic
        if (Mathf.Abs(moveData.Horizontal) > 1000 || Mathf.Abs(moveData.Vertical) > 1000)
        {
            Debug.LogWarning("Invalid input detected.");
            return false;
        }
        return true;
    }

    private void TimeManager_OnPostTick()
    {
        if (base.IsServer)
        {
            var reconcileData = new ReconcileData
            {
                Position = _predictionRigidbody.Rigidbody.position,
                Velocity = _predictionRigidbody.Rigidbody.linearVelocity
            };
            reconcileData.SetTick(base.TimeManager.Tick);
            Reconcile(reconcileData);
        }
    }

    private void BuildMoveData(out MoveData moveData)
    {
        moveData = new MoveData
        {
            Horizontal = (short)(_inputDirection.x * 1000),
            Vertical = (short)(_inputDirection.z * 1000),
            IsSprinting = _isSprinting,
            IsJumping = _isJumping
        };
        moveData.SetTick(base.TimeManager.LocalTick);
    }

    private void ReplayInputs(uint startTick)
    {
        for (uint tick = startTick; tick < base.TimeManager.LocalTick; tick++)
        {
            int cacheIndex = (int)(tick % StateCacheSize);

            if (_inputCache[cacheIndex].GetTick() == tick)
            {
                // Replay inputs as normal.
                Move(_inputCache[cacheIndex], ReplicateState.ReplayedCreated);
            }
            else
            {
                Debug.LogWarning("Extrapolating inputs due to missing data.");
                // Extrapolate based on the last known input.
                Move(_inputCache[(int)((tick - 1) % StateCacheSize)], ReplicateState.CurrentCreated);
            }
        }
    }

    [Replicate]
    private void Move(MoveData moveData, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
    {
        if (state.IsFuture())
            return;

        float delta = (float)base.TimeManager.TickDelta;

        Vector3 inputDirection = new Vector3(moveData.Horizontal / 1000f, 0, moveData.Vertical / 1000f);
        Vector3 velocity = inputDirection * (moveData.IsSprinting ? moveSpeed * sprintMultiplier : moveSpeed);

        if (moveData.IsJumping && IsGrounded())
        {
            _predictionRigidbody.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        }

        velocity += Physics.gravity * delta;
        _predictionRigidbody.Velocity(velocity);
        _predictionRigidbody.Simulate();
    }

    private bool IsGrounded()
    {
        return Physics.Raycast(transform.position, Vector3.down, 1.1f, groundLayer);
    }

    public override void CreateReconcile()
    {
        var reconcileData = new ReconcileData
        {
            Position = _predictionRigidbody.Rigidbody.position,
            Velocity = _predictionRigidbody.Rigidbody.linearVelocity
        };
        reconcileData.SetTick(base.TimeManager.Tick);
        Reconcile(reconcileData);
    }

    [Reconcile]
    private void Reconcile(ReconcileData data, Channel channel = Channel.Unreliable)
    {
        uint serverTick = data.GetTick();
        uint adjustedServerTick = serverTick + (uint)(base.TimeManager.RoundTripTime / base.TimeManager.TickDelta);

        _predictionRigidbody.Rigidbody.position = Vector3.Lerp(
            _predictionRigidbody.Rigidbody.position,
            data.Position,
            reconcileInterpolationFactor
        );

        _predictionRigidbody.Rigidbody.linearVelocity = Vector3.Lerp(
            _predictionRigidbody.Rigidbody.linearVelocity,
            data.Velocity,
            reconcileInterpolationFactor
        );

        _predictionRigidbody.ClearPendingForces();
        ReplayInputs(adjustedServerTick);
    }
}

public struct MoveData : IReplicateData
{
    public short Horizontal;
    public short Vertical;
    public bool IsSprinting;
    public bool IsJumping;

    private uint _tick;

    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}

public struct ReconcileData : IReconcileData
{
    public Vector3 Position;
    public Vector3 Velocity;

    private uint _tick;

    public void Dispose() { }
    public uint GetTick() => _tick;
    public void SetTick(uint value) => _tick = value;
}
