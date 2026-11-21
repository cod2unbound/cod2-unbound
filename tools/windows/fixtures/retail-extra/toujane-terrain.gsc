oracleGround(position, player)
{
    floor = bulletTrace(position + (0, 0, 48), position - (0, 0, 80), false, player);
    return floor["position"] + (0, 0, 2);
}

oracleFixture()
{
    wait 6.0;
    players = getentarray("player", "classname");
    for (i = 0; i < players.size; i++)
    {
        player = players[i];
        if (!isAlive(player))
            continue;
        switch (player getEntityNumber())
        {
        case 0:
            player setOrigin(oracleGround((842, 789, -19), player));
            player setPlayerAngles((-40, 180, 0));
            break;
        case 1:
            player setOrigin(oracleGround((1102, 1747, -16), player));
            player setPlayerAngles((-40, 0, 0));
            break;
        case 2:
            player setOrigin(oracleGround((2337, 2238, 57), player));
            player setPlayerAngles((0, -60, 0));
            break;
        case 3:
            player setOrigin(player.origin + (0, 0, 384));
            break;
        }
    }
}
