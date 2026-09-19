using System.Text.Json;

namespace VsBotKit.Agent;

/// <summary>
/// Инструмент для нейросети: имя, описание, схема аргументов и то, что бот
/// делает. Это ровно тот же слой действий, которым пользуется обычная роль —
/// нейросети он просто описан словами.
/// </summary>
public sealed class BotTool
{
    /// <summary>Имя (латиница, цифры, _ — таково требование API моделей).</summary>
    public required string Name { get; init; }

    /// <summary>Что делает инструмент и КОГДА его звать — модель читает это.</summary>
    public required string Description { get; init; }

    /// <summary>JSON-схема аргументов.</summary>
    public string Schema { get; init; } = """{"type":"object","properties":{}}""";

    /// <summary>Само действие. Возвращает текст результата — его увидит модель.</summary>
    public required Func<JsonElement, CancellationToken, Task<string>> RunAsync { get; init; }
}

/// <summary>
/// Набор инструментов бота. <see cref="Standard"/> собирает готовый набор
/// поверх подсистем библиотеки — с ним нейросеть сразу умеет ходить, смотреть,
/// говорить, копать, драться, есть и прятаться. Роль добавляет свои
/// (например, торговые) одной строкой.
/// </summary>
public sealed class BotToolbox : List<BotTool>
{
    public BotToolbox Add(string name, string description, string schema,
        Func<JsonElement, CancellationToken, Task<string>> run)
    {
        Add(new BotTool { Name = name, Description = description, Schema = schema, RunAsync = run });
        return this;
    }

    /// <summary>Простой инструмент без аргументов.</summary>
    public BotToolbox Add(string name, string description, Func<CancellationToken, Task<string>> run) =>
        Add(name, description, """{"type":"object","properties":{}}""", (_, ct) => run(ct));

    private const string NoArgs = """{"type":"object","properties":{}}""";

    /// <summary>
    /// Стандартный набор: всё, что умеет библиотека, в виде инструментов.
    /// Координаты — В СИСТЕМЕ ИГРОКА (как их показывает игра), потому что
    /// именно ими нейросети называют места люди в чате.
    /// </summary>
    public static BotToolbox Standard(VsBot bot)
    {
        var tools = new BotToolbox();

        tools.Add("look", "Осмотреться: здоровье, сытость, время, погода, что вокруг, " +
                          "кто рядом, ближайшие таблички и сундуки. Зови первым делом, " +
                          "если не знаешь обстановки.",
            _ => Task.FromResult(BotSnapshot.Describe(bot)));

        tools.Add("say", "Сказать в общий чат. Так бот разговаривает с игроками.",
            """{"type":"object","properties":{"text":{"type":"string","description":"Что сказать"}},"required":["text"]}""",
            async (args, _) =>
            {
                string text = Str(args, "text") ?? "";
                if (text.Length == 0)
                    return "пустое сообщение не отправлено";
                await bot.SayAsync(text);
                return "сказал: " + text;
            });

        tools.Add("goto", "Дойти до точки. Координаты как в игре (те, что видит игрок). " +
                          "y можно не указывать — тогда бот сам найдёт высоту.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"seconds":{"type":"number","description":"Сколько максимум идти, по умолчанию 120"}},"required":["x","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                double seconds = Num(args, "seconds") ?? 120;
                bool ok = Num(args, "y") is null
                    ? await bot.Movement.MoveToSmartAsync(wx, wz, 0.6, ct)
                    : await bot.Movement.TravelToAsync(
                        new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz)), seconds, ct);
                return (ok ? "пришёл, стою на " : "не дошёл, стою на ") + bot.PlayerCoordsHere();
            });

        tools.Add("climb_to", "Забраться НА ВЫСОТУ: дойти как можно ближе, а где дороги наверх " +
                              "нет — достроить себе столб под ногами (блоки тратятся из сумок, " +
                              "как у игрока). Зови, когда цель выше и обычный goto не добрался. " +
                              "Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"block":{"type":"string","description":"чем столбиться, часть кода блока; можно не указывать"},"seconds":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                var got = await bot.Climbing.ClimbToAsync(pos, Num(args, "seconds") ?? 180, ct,
                    Str(args, "block") ?? "");
                return got + $" ({bot.PlayerCoordsHere()})";
            });

        tools.Add("find_ore", "Что за руду ВИДНО вокруг. Бот смотрит как человек — только то, " +
                              "у чего есть открытая грань (пещера, обрыв, стена шахты), " +
                              "а не сквозь камень.",
            """{"type":"object","properties":{"radius":{"type":"number"},"metal":{"type":"string","description":"часть названия металла, например copper"}}}""",
            (args, _) =>
            {
                var found = bot.Ores.Find((int)(Num(args, "radius") ?? 24), Str(args, "metal") ?? "", 8);
                return Task.FromResult(found.Count == 0
                    ? "руды на виду нет"
                    : string.Join("; ", found.Select(f =>
                        $"{f.Metal} {f.Richness} ({bot.PlayerCoords(f.Pos.X, f.Pos.Y, f.Pos.Z)}, {f.Distance:0} бл)")));
            });

        tools.Add("mine_vein", "Дойти до ближайшей ВИДИМОЙ руды и выработать жилу целиком, " +
                               "подбирая добычу. Нужна кирка подходящего тира.",
            """{"type":"object","properties":{"radius":{"type":"number"},"metal":{"type":"string"}}}""",
            async (args, ct) =>
            {
                var got = await bot.Ores.MineNearestAsync((int)(Num(args, "radius") ?? 24),
                    Str(args, "metal") ?? "", ct);
                return got.ToString();
            });

        tools.Add("build_ladder_up", "Построить ЛЕСТНИЦУ по стене и подняться по ней. " +
                                     "В отличие от столба, по лестнице можно спуститься обратно, " +
                                     "и она не оставляет палку посреди мира. Нужна стена рядом " +
                                     "и лестницы в сумках.",
            """{"type":"object","properties":{"height":{"type":"number","description":"на сколько блоков подняться"}},"required":["height"]}""",
            async (args, ct) =>
            {
                var got = await bot.Scaffolding.LadderUpAsync((int)(Num(args, "height") ?? 4), ct);
                return got + $" ({bot.PlayerCoordsHere()})";
            });

        tools.Add("get_out_up", "Выбраться наверх любым способом: лестницей, если есть стена и " +
                                "лестницы, иначе столбом. Зови, когда бот в яме или в шахте.",
            """{"type":"object","properties":{"height":{"type":"number"}},"required":["height"]}""",
            async (args, ct) =>
            {
                var got = await bot.Scaffolding.GetOutAsync((int)(Num(args, "height") ?? 4), ct);
                return got + $" ({bot.PlayerCoordsHere()})";
            });

        tools.Add("descend_to", "Спуститься к точке НИЖЕ: сначала дорогой, а если дороги вниз " +
                                "нет — разобрать под собой то, по чему поднялся. Зови, когда бот " +
                                "наверху и обычный goto не находит пути. Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"seconds":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                var got = await bot.Climbing.DescendToAsync(pos, Num(args, "seconds") ?? 180, ct);
                return got + $" ({bot.PlayerCoordsHere()})";
            });

        tools.Add("approach_player", "Подойти к игроку по имени (он должен быть в зоне видимости).",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            async (args, ct) =>
            {
                string name = Str(args, "name") ?? "";
                var target = bot.Entities.All.FirstOrDefault(e =>
                    e.IsPlayer && e.PlayerName != null &&
                    e.PlayerName.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return $"игрока \"{name}\" не вижу";
                long id = target.Id;
                bool ok = await bot.Movement.ApproachAsync(
                    () => bot.Entities.Get(id) is { } t ? (t.X, t.Y, t.Z) : null, 1.6, 120, ct);
                return ok ? $"стою рядом с {target.PlayerName}" : "не смог подойти";
            });

        tools.Add("read_signs", "Прочитать надписи на табличках вокруг (радиус в блоках).",
            """{"type":"object","properties":{"radius":{"type":"number"}}}""",
            (args, _) =>
            {
                int r = (int)(Num(args, "radius") ?? 12);
                var signs = bot.Read.SignsNear(r).ToList();
                if (signs.Count == 0)
                    return Task.FromResult("табличек рядом нет");
                return Task.FromResult(string.Join("\n", signs.Select(s =>
                    $"({bot.PlayerCoords(s.Pos.X, s.Pos.Y, s.Pos.Z)}): {string.Join(" / ", s.Lines)}")));
            });

        tools.Add("inspect_container", "Честно осмотреть сундук: подойти, открыть, прочитать, закрыть. " +
                                       "Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                var content = await bot.Read.InspectAsync(pos, ct);
                if (content == null)
                    return "сундук не открылся (далеко или приват)";
                var items = content.Filled.Select(s => $"{s.Code} x{s.Count}").ToList();
                return items.Count == 0 ? "сундук пуст" : string.Join(", ", items);
            });

        tools.Add("inventory", "Что у бота с собой и что в руке.",
            _ => Task.FromResult(BotSnapshot.DescribeInventory(bot)));

        tools.Add("equip", "Взять предмет в руку по части его кода (например \"pickaxe\", \"bread\").",
            """{"type":"object","properties":{"item":{"type":"string"}},"required":["item"]}""",
            async (args, ct) =>
            {
                string code = Str(args, "item") ?? "";
                var slot = await bot.Hands.TakeToHandAsync(code, ct);
                return slot is null ? $"предмета \"{code}\" нет" : $"в руке: {bot.Hands.Held?.Code}";
            });

        tools.Add("mine", "Сломать блок честно: подойти, взять нужный инструмент, потратить положенное время. " +
                          "Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                var result = await bot.Mining.BreakAsync(pos, ct);
                if (result.Success)
                    // Умением правит внешний клиент, и тела оно НЕ держит —
                    // значит подбор берёт владение сам (см. PickupTakesOwnBody)
                    await bot.Mining.CollectDropsAsync(5, 10, ct, callerHoldsBody: false);
                return result.ToString();
            });

        tools.Add("place", "Поставить блок из инвентаря. Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"block":{"type":"string","description":"часть кода блока"}},"required":["x","y","z","block"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                return (await bot.Mining.PlaceAsync(pos, Str(args, "block") ?? "", ct: ct)).ToString();
            });

        tools.Add("use_block", "Использовать блок правой кнопкой: дверь, рычаг, костёр. Координаты как в игре.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"hold_seconds":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = ToWorld(bot, Num(args, "x") ?? 0, Num(args, "y"), Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                if (!await bot.Hands.ReachForAsync(pos, ct: ct))
                    return "не дотянуться";
                await bot.Hands.UseBlockAsync(pos, Num(args, "hold_seconds") ?? 0, ct: ct);
                return "использовал";
            });

        tools.Add("attack", "Ударить существо по коду (\"drifter\", \"wolf\") или игрока по имени. " +
                            "Бот сам подойдёт на дистанцию удара.",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""",
            async (args, ct) =>
            {
                string mask = Str(args, "target") ?? "";
                if (bot.Self.Position is not { } me)
                    return "не знаю, где я";
                // ЖИВОЕ И ТОЛЬКО ЖИВОЕ — ТЕМ ЖЕ ПРАВИЛОМ, ЧТО И НАСТОЯЩИЙ БОЙ
                // (<see cref="CombatTargets.CanBeFought"/>).
                //
                // ЗДЕСЬ НЕ БЫЛО НИ ОДНОЙ ПРОВЕРКИ ЖИВОСТИ ВОВСЕ — а это рука
                // нейросети, третья дверь в бой рядом с самообороной и
                // «!убей». На «attack bowtorn» под маску попадали и боуторн, и
                // его стрела «bowtornprojectile», а стрела почти всегда ближе:
                // она втыкается рядом с ботом. То есть модели была отдана ровно
                // та же беда 17.08, только приказ шёл от неё.
                //
                // Правило одно на всех, и на игрока тоже: у игрока здоровье
                // есть и неживым его не объявляли, так что бить его команда
                // по-прежнему может, а вот труп игрока (нуль от сервера) —
                // уже нет, и это правильно.
                var victim = bot.Entities.Nearby(me.X, me.Y, me.Z, 24)
                    .Where(e => e.Id != bot.Entities.OwnEntityId &&
                                (e.Code.Contains(mask, StringComparison.OrdinalIgnoreCase) ||
                                 (e.PlayerName?.Contains(mask, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                                CombatTargets.CanBeFought(
                                    bot.EntityTypes.Inanimate(e.Code), e.Health))
                    .OrderBy(e => e.DistanceTo(me.X, me.Y, me.Z))
                    .FirstOrDefault();
                if (victim == null)
                    return $"живого \"{mask}\" рядом нет";
                long id = victim.Id;
                await bot.Movement.ApproachAsync(
                    () => bot.Entities.Get(id) is { } t ? (t.X, t.Y, t.Z) : null, 2.0, 20, ct);
                if (bot.Entities.Get(id) is { } near)
                {
                    await bot.Movement.FaceAsync(near.X, near.Z, ct);
                    await bot.Actions.AttackEntityAsync(id);
                    return $"ударил {victim.Code}";
                }
                return "цель пропала";
            });

        tools.Add("eat", "Поесть: бот сам выберет самую питательную еду из инвентаря.",
            async ct =>
            {
                var food = bot.Self.FindBestItem(s => s.Code != null ? bot.World.GetSatietyByCode(s.Code) : 0);
                if (food == null)
                    return "еды нет";
                await bot.Hands.UseItemAsync(s => s.Code == food.Content.Code, 2.0, ct);
                return $"поел {food.Content.Code}";
            });

        tools.Add("forage", "Сходить за дикой едой: ягоды, грибы. Возвращает, сколько мест обобрано.",
            """{"type":"object","properties":{"radius":{"type":"number"}}}""",
            async (args, ct) =>
            {
                // Причину отказа отдаём ИИ как есть: «неспелый малинник» и
                // «еды вокруг нет» ведут к разным решениям
                var got = await bot.Foraging.GatherAsync(
                    (int)(Num(args, "radius") ?? 32), maxPlaces: 6, maxSeconds: 180, anchor: null, ct: ct);
                return got.Reason;
            });

        tools.Add("campfire", "Развести костёр рядом с собой (нужны дрова, сухая трава и огниво).",
            async ct =>
            {
                var pit = await bot.Fire.MakeCampfireAsync(ct);
                return pit is { } p ? $"костёр на ({bot.PlayerCoords(p.X, p.Y, p.Z)})" : "костёр развести не вышло";
            });

        tools.Add("shelter", "Спрятаться от временной бури: уйти домой и закрыть дверь либо " +
                             "вырыть нишу и замуроваться.",
            async ct => await bot.Shelter.HideAsync(120, ct) ? "укрылся" : "укрыться не вышло");

        tools.Add("wait", "Подождать (секунды). Полезно, когда надо дать миру измениться.",
            """{"type":"object","properties":{"seconds":{"type":"number"}},"required":["seconds"]}""",
            async (args, ct) =>
            {
                double s = Math.Clamp(Num(args, "seconds") ?? 5, 0.5, 60);
                await Task.Delay(TimeSpan.FromSeconds(s), ct);
                return $"подождал {s:0.#} с";
            });

        // ---- копательское: карьеры, ходы, дороги, наряды ----
        //
        // Всё это уже умеет библиотека; здесь оно просто названо словами,
        // чтобы им могли распорядиться и нейросеть, и окно настроек, и
        // внешний клиент — не заводя себе третьего списка умений

        tools.Add("dig_order",
            "НАРЯД: уйти и принести столько-то руды или ресурса. Сам снарядится, " +
            "возьмёт что видно, при нужде уйдёт в разведку под землю, при полной " +
            "сумке сходит на склад. Считает ПРИБАВКУ В СУМКЕ, а не удары киркой. " +
            "Металл можно называть по-русски: медь, олово, железо.",
            """{"type":"object","properties":{"what":{"type":"string","description":"что нести: медь, олово, железо, или код предмета"},"count":{"type":"number"}},"required":["what","count"]}""",
            async (args, ct) =>
            {
                // ЧЕРЕЗ ВОЗВРАТ, а не прямо в наряд. Прямой вызов означал ровно
                // то, на что жаловался заказчик — «бой отменяет некоторые
                // задания, а не временно их паузит»: перебей нейросетевой наряд
                // еда или дрифтер, он умирал целиком и отчитывался «принёс 0 из
                // 10», а модель читала это как «руды тут нет» и уводила бота в
                // другое место. У Копателя этот шов давно сшит
                // (SurvivorRole, навык «наряд»), а у ИИ-роли остался разорванным —
                // второй механизм заводить не надо, надо позвать тот же
                var got = await OrderResume.KeepFetchingAsync(bot.Context,
                    Str(args, "what") ?? "", (int)(Num(args, "count") ?? 0),
                    BodyArbiter.Importance.Command, bot.Log, ct);
                return got.ToString();
            });

        // ЧТО ИМЕННО ОБЕЩАТЬ ПРО ЦЕЛЫЕ БЛОКИ. Раньше здесь стояло безусловное
        // «половина объёма выходит целыми блоками», а способ съёма выбирает
        // человек ручкой роли (Quarry.Cut): при «ровной яме целиком» целых
        // блоков НОЛЬ (Quarry.PlanWholeVolume отдаёт Harvest = []). Нейросеть
        // планирует на числах — пообещав ей камень, которого не будет, мы
        // получаем «строю из целого камня» поверх мешка крошки. Поэтому
        // описание называет ОБА способа и отсылает за фактом к ответу: там
        // сказано, сколько вышло целыми, а сколько крошкой
        tools.Add("quarry",
            "Выкопать КАРЬЕР вокруг себя. Способ съёма выбирает человек настройкой роли " +
            "(«ровная яма целиком»): шахматкой — четверть объёма выходит ЦЕЛЫМИ блоками, " +
            "но пока яма не добита, в ней стоит решётка недобранных клеток; целиком — " +
            "ровная яма, зато целых блоков НЕ БУДЕТ ВОВСЕ, весь камень крошкой. " +
            "Сколько вышло целыми и сколько крошкой — сказано в ответе; на целый камень " +
            "рассчитывай только по нему. Оставляет пандус, чтобы выбраться.",
            """{"type":"object","properties":{"size":{"type":"number","description":"сторона в блоках"},"depth":{"type":"number","description":"слоёв вглубь"}}}""",
            async (args, ct) =>
            {
                if (bot.Quarry.TopCentreUnderSelf is not { } centre)
                    return "не знаю, где стою";
                var got = await bot.Quarry.QuarryAsync(centre, (int)(Num(args, "size") ?? 7),
                    (int)(Num(args, "depth") ?? 3), ct);
                return got.ToString();
            });

        tools.Add("tunnel_to",
            "Прокопать ХОД до точки, куда пешком не пройти. Ход идёт лесенкой, " +
            "чтобы можно было вернуться; заделывает пол над пустотой и ставит факелы. " +
            "Останавливается при лаве и воде.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var cell = bot.WorldCell(Num(args, "x") ?? 0, Num(args, "y") ?? 0,
                    Num(args, "z") ?? 0);
                var got = await bot.Tunnel.DigToAsync(cell, ct);
                return got.ToString();
            });

        tools.Add("prospect",
            "РАЗВЕДКА: уйти под землю и поискать руду там, где её не видно сверху — " +
            "ствол вниз, потом штреки в стороны. Зови, когда find_ore ничего не нашёл.",
            """{"type":"object","properties":{"metal":{"type":"string","description":"что искать: медь, олово; пусто — любую руду"},"depth":{"type":"number","description":"на сколько блоков опуститься"}}}""",
            async (args, ct) =>
            {
                if (Num(args, "depth") is { } depth)
                    bot.Prospecting.DepthBelowStart = (int)depth;
                var got = await bot.Prospecting.SearchAsync(Str(args, "metal") ?? "", ct);
                return got.ToString();
            });

        tools.Add("pave_to",
            "Проложить ДОРОГУ до точки: замостить путь тем, что есть в сумках, " +
            "чтобы дальше по нему ходить быстрее.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var cell = bot.WorldCell(Num(args, "x") ?? 0, Num(args, "y") ?? 0,
                    Num(args, "z") ?? 0);
                var got = await bot.Roads.PaveToAsync(cell, ct);
                return got.ToString();
            });

        // Дорога ОТРЕЗКОМ. «pave_to» выше мостит от текущего места и полосой
        // в одну клетку, потому что маршрута шире у поиска пути не бывает.
        // Настоящая дорога — это две точки и ширина, и перепады она БЕРЁТ,
        // а не обходит
        tools.Add("build_road",
            "Построить ДОРОГУ отрезком: от точки до точки полосой заданной ширины. " +
            "Сперва бот проходит и выкапывает весь ход, потом возвращается и мостит. " +
            "Перепад берётся ступенькой «блок + полублок на нём».",
            """{"type":"object","properties":{"x1":{"type":"number"},"y1":{"type":"number"},"z1":{"type":"number"},"x2":{"type":"number"},"y2":{"type":"number"},"z2":{"type":"number"},"width":{"type":"number","description":"ширина полотна, 1..9"}},"required":["x1","y1","z1","x2","y2","z2"]}""",
            async (args, ct) =>
            {
                var from = bot.WorldCell(Num(args, "x1") ?? 0, Num(args, "y1") ?? 0,
                    Num(args, "z1") ?? 0);
                var to = bot.WorldCell(Num(args, "x2") ?? 0, Num(args, "y2") ?? 0,
                    Num(args, "z2") ?? 0);
                var got = await bot.Roads.BuildAsync(from, to,
                    (int)(Num(args, "width") ?? bot.Roads.Width), ct);
                return got.ToString();
            });

        tools.Add("haul_to_depot",
            "Отнести добытое на склад и вернуться в забой. Инструменты, факелы и еда " +
            "остаются при себе. Склада нет — скажет об этом.",
            async ct => (await bot.Depot.HaulAsync(ct)).ToString());

        tools.Add("check_stock",
            "Что с запасами: кирка, факелы, еда. Показывает, чего не хватает.",
            _ => Task.FromResult(bot.Stock.Missing().Count == 0
                ? $"запасы в порядке: {bot.Stock}"
                : "не хватает: " + string.Join(", ", bot.Stock.Missing())));

        tools.Add("replenish_stock",
            "Пополнить запасы: сперва сделать из своего по рецептам, потом добыть.",
            async ct => (await bot.Stock.ReplenishAsync(ct)).ToString());

        return tools;
    }

    // ---- разбор аргументов и координат ----

    /// <summary>Число из аргументов модели (null, если не передано).</summary>
    public static double? Num(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Строка из аргументов модели.</summary>
    public static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Координаты игрока → мировые. Игра показывает X и Z от точки спавна,
    /// а Y абсолютный; бот внутри живёт в мировых. Без перевода человек и бот
    /// говорят о разных местах.
    /// </summary>
    public static (double X, double Y, double Z) ToWorld(VsBot bot, double x, double? y, double z)
    {
        // Складывает WorldOrigin, и сдвигом служит КЛЕТКА спавна
        // (VsBot.CoordOffset). Здесь стояло «x + spawn.X», а сервер шлёт спавн
        // серединой блока (512000,5): бот вставал на полблока в стороне от
        // места, которое назвала нейросеть, — молча и всегда
        var (wx, wy, wz) = WorldOrigin.ToWorld(
            x, y ?? bot.Self.Position?.Y ?? 0, z, bot.CoordOffset);
        return (wx, wy, wz);
    }
}
