using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class keyInfo : MonoBehaviour
{
    public string keyName = "1";
    public Material currentMaterial;
    public Material offMaterial;
    public Material onMaterial;
    public TextMeshPro keyLabel;
    public bool alwaysOn = false;
    public bool overrideNameChange = false;

    private bool alreadyBig = false;

    // Start is called before the first frame update
    void Start()
    {
        currentMaterial = offMaterial;
        if (keyLabel != null && !overrideNameChange)
        {
            keyLabel.SetText(keyName);
        }
        
    }

    public void MakeBigger()
    {
        if (!alreadyBig)
        {
            this.transform.localScale *= 1.2f;
            alreadyBig = true;
        }
        
    }

    public void MakeSmaller()
    {
        if (alreadyBig)
        {
            this.transform.localScale /= 1.2f;
            alreadyBig = false;
        }  
    }

    public void ChangeToOnMaterial()
    {
        if (!alwaysOn)
        {
            currentMaterial = onMaterial;
            this.GetComponent<MeshRenderer>().material = currentMaterial;
        }
    }

    public void ChangeToOffMaterial()
    {
        if (!alwaysOn)
        {
            currentMaterial = offMaterial;
            this.GetComponent<MeshRenderer>().material = currentMaterial;
        }
    }

    public void KeepOn()
    {
        alwaysOn = true;
        currentMaterial = onMaterial;
        this.GetComponent<MeshRenderer>().material = currentMaterial;
    }

    public void TurnOff()
    {
        alwaysOn = false;
        currentMaterial = offMaterial;
        this.GetComponent<MeshRenderer>().material = currentMaterial;
    }
}
