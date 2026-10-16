# Golden saves

One folder per environment, copied fresh into `<install>\saves\uitest` by run-test.ps1 before
every test (so a test can never pollute the next one):

    saves/stock-sandbox/            for D:\Games\KSP-Vanilla (RO/RSS/FAR/MechJeb, no RP-1)
        persistent.sfs              a sandbox save, all facilities usable
        Ships/VAB/UITest_Rocket.craft   small rocket with a MechJeb-capable command part
        Ships/SPH/UITest_Plane.craft    an aircraft for Ctrl_BarrelRoll
    saves/rp1-career/               for D:\Games\KSP-RP1-Stock (full RP-1)
        persistent.sfs              an RP-1 career with the VAB unlocked, an LC that can build the craft, funds
        Ships/VAB/UITest_Sounding.craft
        Ships/VAB/UITest_Capsule.craft  for Kct_SimConfig_Reentry

Copies of existing saves are fine. Pick the template with `-Save <name>`; override craft names with
`-Params @{ craft = 'MyCraft' }`.
