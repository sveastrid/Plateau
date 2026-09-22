using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class pointerControl : MonoBehaviour
{
    public string currentLetter = "";
    public keyInfo currentKey;
    public GameObject currentGamepiece;

    private HashSet<Collider> overlappedColliders = new HashSet<Collider>();

    void Update()
    {
        // 1. Clean up destroyed or inactive colliders
        overlappedColliders.RemoveWhere(col => col == null || !col.gameObject.activeInHierarchy || !col.enabled);

        // 2. Find closest key and closest gamepiece
        Vector3 origin = transform.parent != null ? transform.parent.position : transform.position;
        
        Collider closestKeyCol = null;
        float closestKeyDist = float.MaxValue;
        
        Collider closestPieceCol = null;
        float closestPieceDist = float.MaxValue;

        foreach (Collider col in overlappedColliders)
        {
            float dist = Vector3.Distance(col.ClosestPoint(origin), origin);
            
            if (col.CompareTag("key"))
            {
                if (dist < closestKeyDist)
                {
                    closestKeyDist = dist;
                    closestKeyCol = col;
                }
            }
            else if (col.CompareTag("gamepiece"))
            {
                if (dist < closestPieceDist)
                {
                    closestPieceDist = dist;
                    closestPieceCol = col;
                }
            }
        }

        // 3. Update currentKey
        keyInfo nextKey = null;
        if (closestKeyCol != null)
        {
            nextKey = closestKeyCol.GetComponent<keyInfo>();
            // If the closest key has no keyInfo component, ignore it.
            if (nextKey == null)
            {
                closestKeyCol = null;
            }
        }

        if (currentKey != nextKey)
        {
            if (currentKey != null)
            {
                currentKey.ChangeToOffMaterial();
            }
            
            currentKey = nextKey;
            
            if (currentKey != null)
            {
                currentKey.ChangeToOnMaterial();
                currentLetter = currentKey.keyName;
            }
            else
            {
                currentLetter = "";
            }
        }

        // 4. Update currentGamepiece
        GameObject nextPiece = null;
        if (closestPieceCol != null)
        {
            nextPiece = closestPieceCol.gameObject;
        }

        if (currentGamepiece != nextPiece)
        {
            if (currentGamepiece != null)
            {
                Transform ring = currentGamepiece.transform.GetChild(1);
                if (ring != null) ring.gameObject.SetActive(false);
            }

            currentGamepiece = nextPiece;

            if (currentGamepiece != null)
            {
                Transform ring = currentGamepiece.transform.GetChild(1);
                if (ring != null) ring.gameObject.SetActive(true);
            }
        }
    }

    public void OnTriggerEnter(Collider col)
    {
        if (col.CompareTag("key") || col.CompareTag("gamepiece"))
        {
            overlappedColliders.Add(col);
        }
    }

    public void OnTriggerExit(Collider col)
    {
        if (col.CompareTag("key") || col.CompareTag("gamepiece"))
        {
            overlappedColliders.Remove(col);
        }
    }
}
