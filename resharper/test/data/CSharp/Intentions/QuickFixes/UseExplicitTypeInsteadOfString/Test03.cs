using UnityEngine;

public class Test03 : MonoBehaviour
{
    class Whatever
    {
        
    }
    
    public void Method()
    {
        var t = GetComponent("Whate{caret}ver");
    }
}