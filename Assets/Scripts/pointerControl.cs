using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class pointerControl : MonoBehaviour
{
    public string currentLetter = "";
    public keyInfo currentKey;

    // BASH's gamepieces, which are triggers with a Rigidbody. They cannot go through PointerBeam
    // instead: that raycast uses QueryTriggerInteraction.Ignore, and relaxing it would make the
    // beam hit its own capsule and both grabber volumes. One target with no distance sorting, as
    // for keys — fine for four well-separated pieces per base, fiddly at the smallest board scale.
    public GameObject currentGamepiece;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void OnTriggerEnter(Collider col)
    {
        if (col.gameObject.tag == "key")
        {
            keyInfo entered = col.gameObject.GetComponent<keyInfo>();
            if (entered == null)
            {
                // Tagged "key" with no keyInfo. Used to be an unguarded dereference, i.e. a
                // NullReferenceException out of a physics callback the first time somebody tags
                // something by hand.
                return;
            }

            currentKey = entered;
            currentKey.ChangeToOnMaterial();
            currentLetter = currentKey.keyName;

            float distance = Vector3.Distance(col.ClosestPoint(this.transform.parent.position), this.transform.parent.position);
            float currentSize = this.transform.GetChild(0).GetComponent<Renderer>().bounds.size.magnitude;
            Vector3 newScale = new Vector3(1, (distance/currentSize), 1);
            this.transform.GetChild(0).localScale = newScale;
            this.transform.GetChild(0).localPosition = new Vector3(0, -1+(distance / currentSize), 0);
            this.transform.GetChild(0).GetChild(0).gameObject.SetActive(true);
        }
        else if (col.gameObject.tag == "gamepiece")
        {
            col.transform.GetChild(1).gameObject.SetActive(true);   // hover ring
            currentGamepiece = col.gameObject;
        }
    }

    public void OnTriggerStay(Collider col)
    {
        if (col.gameObject.tag == "key")
        {
            if (currentLetter == "")
            {
                keyInfo resting = col.gameObject.GetComponent<keyInfo>();
                if (resting == null)
                {
                    return;
                }

                currentKey = resting;
                currentKey.ChangeToOnMaterial();
                currentLetter = currentKey.keyName;
                this.transform.GetChild(0).GetChild(0).gameObject.SetActive(true);
            }
            float distance = Vector3.Distance(col.ClosestPoint(this.transform.GetChild(0).GetChild(0).position), this.transform.parent.position);
            float currentSize = this.transform.GetChild(0).GetComponent<Renderer>().bounds.size.magnitude;
            Vector3 newScale = new Vector3(1, this.transform.GetChild(0).localScale.y*(distance / currentSize), 1);
            this.transform.GetChild(0).localScale = newScale;
            this.transform.GetChild(0).localPosition = new Vector3(0, - (1 - this.transform.GetChild(0).localScale.y * (distance / currentSize)), 0);
        }
    }


    public void OnTriggerExit(Collider col)
    {
        if (col.gameObject.tag == "key")
        {
            keyInfo leaving = col.gameObject.GetComponent<keyInfo>();
            if (leaving != null)
            {
                leaving.ChangeToOffMaterial();
            }

            // Only forget the current key if it is the one being left. Rows in a ScrollList are
            // adjacent — 84 units of pitch against a 76-unit collider — so sweeping the beam down
            // a list fires Enter(next) and Exit(previous) in the same physics step, in an order
            // Unity does not define. This used to assign currentKey from the collider that was
            // LEAVING and then null it unconditionally, which threw away the key the beam had
            // already moved onto; Panel.Update reads currentKey on trigger release and cancels the
            // press when it is null, so a release inside that window did nothing at all and said
            // nothing about it. See docs/UIBugFixes.md §7.
            if (currentKey != leaving)
            {
                return;
            }

            currentLetter = "";
            currentKey = null;
            this.transform.GetChild(0).localScale = new Vector3(1, 1, 1);
            this.transform.GetChild(0).localPosition = new Vector3(0, 0, 0);
            this.transform.GetChild(0).GetChild(0).gameObject.SetActive(false);
        }
        else if (col.gameObject.tag == "gamepiece")
        {
            col.transform.GetChild(1).gameObject.SetActive(false);  // hover ring
            currentGamepiece = null;
        }
    }
}
