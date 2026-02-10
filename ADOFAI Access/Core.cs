using MelonLoader;
using DavyKager;

[assembly: MelonInfo(typeof(ADOFAI_Access.Core), "ADOFAI Access", "1.0.0", "Molitvan", null)]
[assembly: MelonGame("7th Beat Games", "A Dance of Fire and Ice")]

namespace ADOFAI_Access
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Tolk.Load();
            LoggerInstance.Msg("ADOFAI Access Loaded");
        }

        public override void OnLateInitializeMelon()
        {
            Tolk.Output("ADOFAI Access loaded");
        }
    }
}