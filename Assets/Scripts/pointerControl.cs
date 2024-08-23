using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class pointerControl : MonoBehaviour
{
    public string currentLetter = "";
    public keyInfo currentKey;

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
            currentKey = col.gameObject.GetComponent<keyInfo>();
            currentKey.ChangeToOnMaterial();
            currentLetter = currentKey.keyName;

            float distance = Vector3.Distance(col.ClosestPoint(this.transform.parent.position), this.transform.parent.position);
            float currentSize = this.transform.GetChild(0).GetComponent<Renderer>().bounds.size.magnitude;
            Vector3 newScale = new Vector3(1, (distance/currentSize), 1);
            this.transform.GetChild(0).localScale = newScale;
            this.transform.GetChild(0).localPosition = new Vector3(0, -1+(distance / currentSize), 0);
            this.transform.GetChild(0).GetChild(0).gameObject.SetActive(true);
        }
    }

    public void OnTriggerStay(Collider col)
    {
        if (col.gameObject.tag == "key")
        {
            if (currentLetter == "")
            {
                currentKey = col.gameObject.GetComponent<keyInfo>();
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
            currentKey = col.gameObject.GetComponent<keyInfo>();
            currentKey.ChangeToOffMaterial();
            currentLetter = "";
            currentKey = null;
            this.transform.GetChild(0).localScale = new Vector3(1, 1, 1);
            this.transform.GetChild(0).localPosition = new Vector3(0, 0, 0);
            this.transform.GetChild(0).GetChild(0).gameObject.SetActive(false);
        }
    }
}
