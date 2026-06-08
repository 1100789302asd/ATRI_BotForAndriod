using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

public class BkVideoPlayer : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    
    public int index;
    RectTransform rt;
    public RawImage img;
    public List<VideoConfig> bkLt;
    VideoPlayer vp;
    void Awake()
    {
        rt=GetComponent<RectTransform>();
        vp=GetComponent<VideoPlayer>();
        img=GetComponent<RawImage>();
        vp.clip=bkLt[index].clip;
        rt.anchoredPosition=bkLt[index].pos;
    }
    
    public void ChangeVideo()
    {
        index+=2;
        index%=bkLt.Count;
        // vp.Pause();
        vp.clip=bkLt[index].clip;
        // vp.Play();
        rt.anchoredPosition=bkLt[index].pos;
        
        
    }
    
}
