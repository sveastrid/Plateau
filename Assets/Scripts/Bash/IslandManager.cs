using Unity.Netcode;
using UnityEngine;

// Attach this to the scene-placed "Islands" GameObject (a child of "Board").
// Requires a sibling NetworkObject component. The four Island prefab instances
// must remain as direct children of this GameObject (order = canonical index).
//
// Board-local from the start, so the port to MRBoardGame2 changed nothing here — and the fact
// that baseExclusionZones below already matched SpawnManager's rebased seat positions exactly is
// what confirmed the rest of that rebasing was right. See BashRoot.
public class IslandManager : NetworkBehaviour
{
    [Header("Playable area (Board-local XZ)")]
    public Vector2 boardMin = new Vector2(-1.3f, -1.3f);
    public Vector2 boardMax = new Vector2(1.3f, 1.3f);

    [Header("Island size")]
    public float minScale = 0.4f;
    public float maxScale = 1.2f;
    // Matches Island.prefab CapsuleCollider radius (0.28). Used to keep islands
    // on the board and apart from each other / from bases.
    public float islandColliderRadius = 0.28f;
    public float edgeMargin = 0.05f;

    [Header("Spacing constraints")]
    // Minimum gap between island edges (not center-to-center).
    public float minSpacing = 0.15f;
    // Board-local XZ of each player's base, taken from SpawnManager.SpawnBaseServerRpc.
    public Vector2[] baseExclusionZones = new Vector2[]
    {
        new Vector2( 0f, -1.4f),
        new Vector2( 0f,  1.4f),
        new Vector2( 1.4f, 0f),
        new Vector2(-1.4f, 0f),
    };
    // Distance from base center that the island edge must stay outside.
    public float baseExclusionRadius = 0.5f;

    [Header("Randomization")]
    public bool randomizeYRotation = true;
    public int maxSamplingAttempts = 100;

    private const float ISLAND_Y = 0.003f;
    private const int ISLAND_COUNT = 4;

    private bool hasBeenRandomized = false;
    private Vector3[] cachedPositions = new Vector3[ISLAND_COUNT];
    private Vector3[] cachedScales = new Vector3[ISLAND_COUNT];
    private float[] cachedYRotations = new float[ISLAND_COUNT];

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        // Late-join sync: clients ask the server for the current layout (if any).
        if (!IsServer)
        {
            RequestCurrentStateServerRpc();
        }
    }

    // Public entry point — called when the player presses "Random Islands" on the shared menu,
    // via MenuControl.HandleKey or ControlListener.RandomizeIslands. Safe for any client to call.
    public void RandomizeIslands()
    {
        RandomizeIslandsServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RandomizeIslandsServerRpc()
    {
        if (transform.childCount < ISLAND_COUNT)
        {
            Debug.LogError($"IslandManager: expected at least {ISLAND_COUNT} island children under '{name}', found {transform.childCount}. Aborting randomization.");
            return;
        }

        GenerateNewLayout();
        hasBeenRandomized = true;
        // Broadcast — this also runs on the host, which is how the host applies.
        RandomizeIslandsClientRpc(cachedPositions, cachedScales, cachedYRotations);
    }

    [ClientRpc]
    private void RandomizeIslandsClientRpc(Vector3[] positions, Vector3[] scales, float[] yRotations)
    {
        ApplyTransforms(positions, scales, yRotations);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestCurrentStateServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!hasBeenRandomized) return;
        SyncCurrentStateClientRpc(cachedPositions, cachedScales, cachedYRotations, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
            }
        });
    }

    [ClientRpc]
    private void SyncCurrentStateClientRpc(Vector3[] positions, Vector3[] scales, float[] yRotations, ClientRpcParams rpcParams = default)
    {
        ApplyTransforms(positions, scales, yRotations);
    }

    private void ApplyTransforms(Vector3[] positions, Vector3[] scales, float[] yRotations)
    {
        int n = Mathf.Min(transform.childCount, Mathf.Min(positions.Length, Mathf.Min(scales.Length, yRotations.Length)));
        for (int i = 0; i < n; i++)
        {
            Transform t = transform.GetChild(i);
            t.localPosition = positions[i];
            t.localScale = scales[i];
            t.localRotation = Quaternion.Euler(0f, yRotations[i], 0f);
        }
    }

    // Generates positions via rejection sampling. Each island must:
    //   - Have its bounding circle inside [boardMin, boardMax]
    //   - Be >= minSpacing edge-to-edge from previously placed islands
    //   - Be >= baseExclusionRadius edge-to-center from every base zone
    // If a position can't be found in maxSamplingAttempts, the last candidate
    // is used and a warning is logged (degrades gracefully, never throws).
    private void GenerateNewLayout()
    {
        float[] halfExtents = new float[ISLAND_COUNT];

        // Hard cap: an island can never be larger than the playable rect itself.
        Vector2 axisHalfExtent = (boardMax - boardMin) * 0.5f;
        float maxAllowedHalfExtent = Mathf.Min(axisHalfExtent.x, axisHalfExtent.y);

        for (int i = 0; i < ISLAND_COUNT; i++)
        {
            float scale = Random.Range(minScale, maxScale);
            float halfExtent = islandColliderRadius * scale + edgeMargin;

            if (halfExtent > maxAllowedHalfExtent)
            {
                halfExtent = maxAllowedHalfExtent;
                scale = Mathf.Max(minScale, (halfExtent - edgeMargin) / islandColliderRadius);
            }

            Vector2 sampleMin = boardMin + new Vector2(halfExtent, halfExtent);
            Vector2 sampleMax = boardMax - new Vector2(halfExtent, halfExtent);

            Vector2 chosen = Vector2.zero;
            bool valid = false;
            for (int attempt = 0; attempt < maxSamplingAttempts; attempt++)
            {
                chosen = new Vector2(
                    Random.Range(sampleMin.x, sampleMax.x),
                    Random.Range(sampleMin.y, sampleMax.y)
                );
                valid = true;

                for (int j = 0; j < i && valid; j++)
                {
                    float required = halfExtents[j] + halfExtent + minSpacing;
                    Vector2 prevXZ = new Vector2(cachedPositions[j].x, cachedPositions[j].z);
                    if (Vector2.Distance(chosen, prevXZ) < required) valid = false;
                }

                for (int k = 0; k < baseExclusionZones.Length && valid; k++)
                {
                    float required = baseExclusionRadius + halfExtent;
                    if (Vector2.Distance(chosen, baseExclusionZones[k]) < required) valid = false;
                }

                if (valid) break;
            }

            if (!valid)
            {
                Debug.LogWarning($"IslandManager: could not find a non-overlapping position for island {i} after {maxSamplingAttempts} attempts; using last candidate.");
            }

            cachedPositions[i] = new Vector3(chosen.x, ISLAND_Y, chosen.y);
            cachedScales[i] = new Vector3(scale, 1f, scale);
            cachedYRotations[i] = randomizeYRotation ? Random.Range(0f, 360f) : 0f;
            halfExtents[i] = halfExtent;
        }
    }
}
