using System;
using System.Collections;
using UnityEngine;

public class VideoCtrl : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public BkVideoPlayer v1,v2;
    public bool isV1Show;
    bool isInTransition;
    void Start()
    {
        // ChangeBk();
    }
    IEnumerator Fade()
    {
        
        float dur=0.5f;
        if(isV1Show)
        {
            
            v2.img.CrossFadeAlpha(1,dur,true);
            v1.img.CrossFadeAlpha(0,dur,true);
            yield return new WaitForSeconds(dur);
            v1.ChangeVideo();
        }
        else
        {
            
            v1.img.CrossFadeAlpha(1,dur,true);
            v2.img.CrossFadeAlpha(0,dur,true);
            yield return new WaitForSeconds(dur);
            v2.ChangeVideo();
        }
        yield return new WaitForSeconds(1.5f);//等待视频加载
        isV1Show=!isV1Show;
        isInTransition=false;
    }
    public void ChangeBk()
    {
        if(!isInTransition)
        {
            isInTransition=true;
            StartCoroutine(Fade());
        }
        
    }
    // Update is called once per frame
    void Update()
    {
        
    }
}
