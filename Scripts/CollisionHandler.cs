using FishNet.Object;
using FishNet.Component.Prediction;
using FishNet.Connection;
using UnityEngine;

public class CollisionHandler : NetworkBehaviour
{
    [SerializeField] private LayerMask rollbackLayer;
    [SerializeField] private Rigidbody _rigidbody;
    [SerializeField] private NetworkCollision _networkCollision;
    [SerializeField] private MeshRenderer _targetMeshRenderer;

    private bool _isInitialized = false;

    private void Awake()
    {
        if (_networkCollision == null)
            _networkCollision = GetComponentInChildren<NetworkCollision>();
        if (_rigidbody == null)
            _rigidbody = GetComponent<Rigidbody>();
        if (_targetMeshRenderer == null)
            _targetMeshRenderer = GetComponentInChildren<MeshRenderer>();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        _isInitialized = true;
        Debug.Log($"[Client] CollisionHandler initialized for Client ID: {Owner.ClientId}");
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();
        if (_networkCollision != null)
            _networkCollision.OnEnter += HandleCollisionEnter;

        if (IsServer)
        {
            CollisionManager.Instance.RegisterCollisionHandler(Owner.ClientId, this);
        }
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        if (_networkCollision != null)
            _networkCollision.OnEnter -= HandleCollisionEnter;
    }

    private void HandleCollisionEnter(Collider otherCollider)
    {
        if (!IsOwner)
            return;

        CollisionHandler other = otherCollider.GetComponentInParent<CollisionHandler>();
        if (other == null || other.Owner.ClientId == Owner.ClientId)
            return;

        Vector3 myVelocity = _rigidbody.linearVelocity;

        ServerUpdateVelocity(Owner.ClientId, myVelocity);
        ServerReportCollision(Owner.ClientId, other.Owner.ClientId);
    }

    [ServerRpc]
    private void ServerUpdateVelocity(int clientId, Vector3 velocity)
    {
        CollisionManager.Instance.UpdateClientVelocity(clientId, velocity);
    }

    [ServerRpc]
    private void ServerReportCollision(int client1Id, int client2Id)
    {
        CollisionManager.Instance.HandleCollision(client1Id, client2Id);
    }

    [TargetRpc]
    public void ApplyCollisionResult(NetworkConnection target, int winnerId, int loserId)
    {
        if (!_isInitialized)
        {
            Debug.LogWarning($"[Client] CollisionHandler is not initialized. Cannot apply collision result for Client ID: {Owner.ClientId}");
            return;
        }

        if (Owner.ClientId == winnerId)
        {
            Debug.Log($"[Client] I am the winner. No action needed. My Client ID: {Owner.ClientId}");
        }
        else if (Owner.ClientId == loserId)
        {
            Debug.Log($"[Client] I am the loser. Disabling my mesh renderer locally. My Client ID: {Owner.ClientId}");

            // Disable the mesh locally
            _targetMeshRenderer.enabled = false;

            // Inform the server to broadcast this change
            ServerNotifyMeshStateChange(true);

            // Re-enable the mesh after 3 seconds
            Invoke(nameof(ReEnableMesh), 3f);
        }
        else
        {
            Debug.Log($"[Client] I am not involved in this collision. Ignoring collision result. My Client ID: {Owner.ClientId}");
        }
    }

    private void ReEnableMesh()
    {
        if (!_isInitialized)
        {
            Debug.LogWarning($"[Client] ReEnableMesh called before initialization for Client ID: {Owner.ClientId}");
            return;
        }

        Debug.Log($"[Client] Re-enabling mesh renderer locally for Client ID: {Owner.ClientId}");
        _targetMeshRenderer.enabled = true;

        // Inform the server to broadcast this change
        ServerNotifyMeshStateChange(false);
    }

    [ServerRpc]
    private void ServerNotifyMeshStateChange(bool disable)
    {
        Debug.Log($"[Server] Received mesh state change request: {(disable ? "Disable" : "Enable")} for Client ID: {Owner.ClientId}");
        NotifyMeshStateChange(disable);
    }

    [ObserversRpc(BufferLast = true, ExcludeOwner = false)]
    private void NotifyMeshStateChange(bool disable)
    {
        Debug.Log($"[ObserversRpc] Mesh state updated: {(disable ? "Disabled" : "Enabled")} for Client ID: {Owner.ClientId}");

        // Only update the local state if it's different
        if (_targetMeshRenderer.enabled == disable)
        {
            _targetMeshRenderer.enabled = !disable;
        }
    }
}
