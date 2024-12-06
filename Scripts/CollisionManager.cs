using FishNet.Object;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FishNet.Connection;

public class CollisionManager : NetworkBehaviour
{
    public static CollisionManager Instance { get; private set; }

    private readonly Dictionary<int, Vector3> clientVelocities = new Dictionary<int, Vector3>();
    private readonly Dictionary<int, CollisionHandler> clientHandlers = new Dictionary<int, CollisionHandler>();
    private readonly HashSet<string> activeCollisions = new HashSet<string>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    [Server]
    public void RegisterCollisionHandler(int clientId, CollisionHandler handler)
    {
        if (!clientHandlers.ContainsKey(clientId))
        {
            clientHandlers[clientId] = handler;
        }
    }

    [Server]
    public void UpdateClientVelocity(int clientId, Vector3 velocity)
    {
        clientVelocities[clientId] = velocity;
    }

    [Server]
    public void HandleCollision(int client1Id, int client2Id)
    {
        string collisionKey = GenerateCollisionKey(client1Id, client2Id);

        if (activeCollisions.Contains(collisionKey))
            return;

        activeCollisions.Add(collisionKey);

        if (!clientVelocities.TryGetValue(client1Id, out Vector3 velocity1) ||
            !clientVelocities.TryGetValue(client2Id, out Vector3 velocity2))
        {
            StartCoroutine(RetryHandleCollision(client1Id, client2Id, collisionKey));
            return;
        }

        ProcessCollision(client1Id, velocity1, client2Id, velocity2, collisionKey);
    }

    private IEnumerator RetryHandleCollision(int client1Id, int client2Id, string collisionKey)
    {
        yield return new WaitForSeconds(0.1f); // Retry after 0.1 seconds

        if (clientVelocities.TryGetValue(client1Id, out Vector3 velocity1) &&
            clientVelocities.TryGetValue(client2Id, out Vector3 velocity2))
        {
            ProcessCollision(client1Id, velocity1, client2Id, velocity2, collisionKey);
        }
        else
        {
            activeCollisions.Remove(collisionKey);
        }
    }

    private void ProcessCollision(int client1Id, Vector3 velocity1, int client2Id, Vector3 velocity2, string collisionKey)
    {
        CollisionResult result = ResolveCollision(client1Id, velocity1, client2Id, velocity2);

        // Winner's side
        if (clientHandlers.TryGetValue(result.WinnerClientId, out CollisionHandler winnerHandler))
        {
            NetworkConnection winnerConnection = winnerHandler.Owner; // Get winner's connection
            winnerHandler.ApplyCollisionResult(winnerConnection, result.WinnerClientId, result.LoserClientId);
        }

        // Loser's side
        if (clientHandlers.TryGetValue(result.LoserClientId, out CollisionHandler loserHandler))
        {
            NetworkConnection loserConnection = loserHandler.Owner; // Get loser's connection
            loserHandler.ApplyCollisionResult(loserConnection, result.WinnerClientId, result.LoserClientId);
        }

        activeCollisions.Remove(collisionKey);
    }

    private CollisionResult ResolveCollision(int client1Id, Vector3 velocity1, int client2Id, Vector3 velocity2)
    {
        return velocity1.magnitude > velocity2.magnitude
            ? new CollisionResult(client1Id, client2Id)
            : new CollisionResult(client2Id, client1Id);
    }

    private string GenerateCollisionKey(int client1Id, int client2Id)
    {
        return client1Id < client2Id ? $"{client1Id}-{client2Id}" : $"{client2Id}-{client1Id}";
    }

    public struct CollisionResult
    {
        public int WinnerClientId;
        public int LoserClientId;

        public CollisionResult(int winnerClientId, int loserClientId)
        {
            WinnerClientId = winnerClientId;
            LoserClientId = loserClientId;
        }
    }
}
