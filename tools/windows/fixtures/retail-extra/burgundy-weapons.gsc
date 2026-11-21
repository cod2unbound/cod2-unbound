oracleFixture()
{
    precacheItem("panzerfaust_mp");
    wait 6.0;

    loadout[0] = "colt_mp";
    loadout[1] = "m1garand_mp";
    loadout[2] = "shotgun_mp";
    loadout[3] = "panzerfaust_mp";
    bySlot = [];
    players = getentarray("player", "classname");
    for (i = 0; i < players.size; i++)
    {
        player = players[i];
        slot = player getEntityNumber();
        if (slot < loadout.size && isAlive(player))
        {
            player takeAllWeapons();
            player giveWeapon(loadout[slot]);
            player giveMaxAmmo(loadout[slot]);
            bySlot[slot] = player;
        }
    }
    wait 4.0;
    for (i = 0; i < loadout.size; i++)
        if (isdefined(bySlot[i]) && bySlot[i] getCurrentWeapon() == loadout[i])
            logPrint("oracle selected " + loadout[i] + "\n");

    wait 1.5;
    if (isdefined(bySlot[2]) && isAlive(bySlot[2]))
    {
        dropped = bySlot[2] dropItem("shotgun_mp");
        if (isdefined(dropped))
            logPrint("oracle dropped shotgun\n");
        if (isdefined(dropped) && isdefined(bySlot[0]) && isAlive(bySlot[0]))
        {
            bySlot[2] setOrigin(bySlot[2].origin + (40, 0, 8));
            wait 0.25;
            bySlot[0] setOrigin(dropped.origin + (0, 0, 8));
            wait 0.75;
            if (bySlot[0] hasWeapon("shotgun_mp"))
                logPrint("oracle picked up shotgun\n");
        }
    }
    if (isdefined(bySlot[1]) && isAlive(bySlot[1]))
    {
        bySlot[1] suicide();
        wait 0.05;
        if (!isAlive(bySlot[1]))
            logPrint("oracle death\n");
        wait 2;
        if (isAlive(bySlot[1]))
            logPrint("oracle respawn\n");
    }
}
