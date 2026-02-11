using DavyKager;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(ADOFAI_Access.Core), "ADOFAI Access", "1.0.0", "Molitvan", null)]
[assembly: MelonGame("7th Beat Games", "A Dance of Fire and Ice")]

namespace ADOFAI_Access
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Tolk.Load();
            HarmonyInstance.PatchAll(typeof(Core).Assembly);
            LoggerInstance.Msg("ADOFAI Access Loaded");
        }

        public override void OnLateInitializeMelon()
        {
            Tolk.Output("ADOFAI Access loaded");
        }

        public override void OnUpdate()
        {
            MenuNarration.Tick();
            AccessibleLevelSelectMenu.Tick();
            LevelDataDump.Tick();
            LevelPreview.Tick();
        }
    }
}
