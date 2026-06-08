using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class DropDownUI : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public Image img;
    public GameObject checkMark;
    public bool initStatus;
    public bool isSwitchUI=true,isInTransition=false,isNcmAcquire;

    
    public Color normalColor,activeColor,clickedColor;
    public NeteaseCloudMusicClient ncm;

    Button bt;

    void Awake()
    {
        bt=GetComponent<Button>();
        if(initStatus)
        {
            SwitchOn();
        }
        
    }

    // Update is called once per frame
    void Update()
    {
        if(isNcmAcquire)
        {
            bt.interactable=ncm.IsNeteaseLoaded;
        }
    }
    IEnumerator ColorShiny(Color targetColor)
    {
        isInTransition=true;
        img.color=clickedColor;
        yield return new WaitForSeconds(0.1f);
        img.color=targetColor;
        isInTransition=false;
    }
    public void SwitchOn()
    {
        img.color=activeColor;
        checkMark.SetActive(true);
        // StartCoroutine(ColorShiny(activeColor));
    }
    public void SwitchOff()
    {
        img.color=normalColor;
        checkMark.SetActive(false);
        // StartCoroutine(ColorShiny(normalColor));
    }
    public void OnClick()
    {
        if(!isInTransition)
        {
            if(isSwitchUI)
            {
                if(checkMark.activeSelf)
                {
                    StartCoroutine(ColorShiny(normalColor));
                    checkMark.SetActive(false);
                }
                else
                {
                    StartCoroutine(ColorShiny(activeColor));
                    checkMark.SetActive(true);
                }
            }
           
        }
        
    }
}
