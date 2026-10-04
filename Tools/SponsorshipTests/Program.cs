using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IDVBuff.Views;
class SponsorshipTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
    static async Task<int> Main()
    {
        try {
            int launches=0,errors=0;bool enabled=true;var states=new List<bool>();
            var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var action=new SponsorshipAction(uri=>{Check(uri.AbsoluteUri=="https://afdian.com/a/xigefuli","fixed approved URL");launches++;return pending.Task;},
                value=>{enabled=value;states.Add(value);},()=>{errors++;return Task.CompletedTask;});
            Check(launches==0 && states.Count==0 && errors==0,"construction does not auto-open or show UI");
            var first=action.ClickAsync();
            Check(launches==1 && !enabled,"first click disables button during launch");
            await action.ClickAsync();await action.ClickAsync();
            Check(launches==1 && !enabled,"repeated clicks cannot reenter pending launch");
            pending.SetResult(true);await first;
            Check(enabled && errors==0 && states.Count==2,"successful launch restores button without error");
            await action.ClickAsync();
            Check(launches==2 && enabled,"later voluntary click is allowed");
            foreach(var throws in new[]{false,true}) {
                launches=0;errors=0;enabled=true;
                var errorWait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                action=new SponsorshipAction(_=>{launches++;if(throws)throw new InvalidOperationException("simulated shell failure");return Task.FromResult(false);},
                    value=>enabled=value,()=>{errors++;return errorWait.Task;});
                var failure=action.ClickAsync();
                Check(enabled && errors==1,"button restored after "+(throws?"exception":"false")+" before error dialog completes");
                await action.ClickAsync();
                Check(launches==1 && errors==1,"no duplicate launch or error while failure dialog pending: "+throws);
                errorWait.SetResult();await failure;await action.ClickAsync();
                Check(launches==2 && enabled && errors==2,"failure releases guard for retry: "+throws);
            }
            enabled=true;launches=0;
            action=new SponsorshipAction(_=>{launches++;return Task.FromResult(false);},v=>enabled=v,()=>throw new InvalidOperationException("simulated dialog failure"));
            for(int i=0;i<2;i++){await action.ClickAsync();}
            Check(enabled && launches==2,"dialog exception does not escape and releases guard with button enabled");
            Console.WriteLine("PASS sponsorship regression total="+passed+"; browser launches simulated only");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
