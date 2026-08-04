using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class UIChangeImg : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    Image img;
    public List<Sprite> spriteLt;

    int nowIndex=0;
    void Start()
    {
        img=GetComponent<Image>();
    }
    public void ChangeImg()
    {
        img.sprite=spriteLt[nowIndex++];
        nowIndex%=spriteLt.Count;
    }
    // Update is called once per frame


    // Update is called once per frame
    void Update()
    {
        
    }
}
