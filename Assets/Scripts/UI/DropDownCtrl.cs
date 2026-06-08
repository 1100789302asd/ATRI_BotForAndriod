using UnityEngine;

public class DropDownCtrl : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public GameObject uiContent;
    bool isOpen;
    public Vector3 originPos;
    void Start()
    {
        originPos=uiContent.transform.position;
        Flyaway();
    }
    public void Flyaway()
    {
        uiContent.transform.position=new Vector3(-1000,0,0);
    }
    public void FlyBack()
    {
        uiContent.transform.position=originPos;
    }
    public void OnClick()
    {
        if(isOpen)
        {
            isOpen=false;
            Flyaway();
        }
        else
        {
            isOpen=true;
            FlyBack();
        }
        
    }
    public void CheckClose()
    {
        //当点到空白区域时关闭
        if(isOpen)
        {
            isOpen=false;
            Flyaway();
        }
    }
}
