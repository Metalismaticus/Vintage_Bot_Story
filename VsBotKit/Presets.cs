namespace VsBotKit;

/// <summary>
/// Готовые наборы способностей — чтобы роль состояла из АЛГОРИТМА и УСЛОВИЙ,
/// а не из сборки бота по винтику.
///
/// Роль-минимум теперь выглядит так:
/// <code>
/// class МояРоль : BotRole
/// {
///     public override void Configure(VsBot bot) => bot.Survive();
/// }
/// </code>
///
/// Каждый пресет возвращает объекты, которые сам создал — роль может их
/// донастроить (пороги, радиусы, дом) или заменить своими.
/// </summary>
public static class Presets
{
    /// <summary>Что собрал пресет выживания — чтобы роль могла подкрутить.</summary>
    public sealed class SurvivalKit
    {
        /// <summary>Возрождение: мёртвому сервер отказывает во всех действиях.</summary>
        public required BehaviorRespawnWhenDead Respawn { get; init; }

        public required BehaviorStayGrounded Grounded { get; init; }

        /// <summary>
        /// Уйти с клетки, которая жжёт: лава, кипяток, огонь, свой же горящий
        /// костёр. Собирается У ВСЕХ и включена по умолчанию — это спасение, а
        /// не хозяйство; выключить её роль вправе (ручка «УходитьИзОгня»), но
        /// решение это громкое и осознанное.
        /// </summary>
        public required BehaviorLeaveHarm LeaveHarm { get; init; }
        public required BehaviorShelterFromStorm Storm { get; init; }
        public required BehaviorAttackHostiles Combat { get; init; }
        public required BehaviorHealWhenHurt Healing { get; init; }
        public required BehaviorEatWhenHungry Eating { get; init; }
        public required BehaviorForageWhenNoFood Foraging { get; init; }

        /// <summary>
        /// ОХОТА РАДИ ЕДЫ — механизм, а не способность: зовут его руки голода
        /// (<see cref="Foraging"/>) последним способом, когда еды нет нигде.
        /// Роль крутит его ручками «Охотиться», «ОхотитьсяНеДальше»,
        /// «СколькоДержатьТелоЗаОхоту».
        /// </summary>
        public required Hunting Hunting { get; init; }
        public required BehaviorKeepGearReady Gear { get; init; }
        public required BehaviorWeaponAtNight NightWeapon { get; init; }
        public required BehaviorLookAlive Alive { get; init; }

        /// <summary>Сон в кровати ночью.</summary>
        public required BehaviorSleepAtNight Sleep { get; init; }

        /// <summary>Одеться и погреться, когда мёрзнет.</summary>
        public required BehaviorKeepWarm Warm { get; init; }

        /// <summary>
        /// Уход из нестабильных мест (по умолчанию ВЫКЛЮЧЕН — решает роль, как
        /// и возврат за вещами: лавочнику посреди торговли бежать некуда).
        /// </summary>
        public required BehaviorSeekStability Stability { get; init; }

        /// <summary>Замена изношенного инструмента.</summary>
        public required BehaviorSwapWornTool WornTool { get; init; }

        /// <summary>Возврат за вещами после смерти (по умолчанию выключен — решает роль).</summary>
        public required BehaviorRecoverLoot Recover { get; init; }

        /// <summary>Факелы в тёмных местах.</summary>
        public required BehaviorKeepLit Lit { get; init; }

        /// <summary>
        /// Доделать начатое (вытесанное лезвие + палка = нож). По умолчанию
        /// ВЫКЛЮЧЕНО: решает роль, как и возврат за вещами.
        /// </summary>
        public required BehaviorFinishStarted Finish { get; init; }

        /// <summary>Сходить за недостающим снаряжением, пока делать нечего.</summary>
        public required BehaviorEquipSelf Equip { get; init; }

        /// <summary>Подобрать свою добычу: сломанное падает на землю и лежит.</summary>
        public required BehaviorPickUpDrops Drops { get; init; }

        /// <summary>Добыча с убитых: подобрать выпавшее и разделать туши.</summary>
        public required BehaviorLootKills Kills { get; init; }

        /// <summary>
        /// Сумки полны — сходить на склад и вернуться к делу (по умолчанию
        /// выключено: решает роль, как и возврат за вещами).
        /// </summary>
        public required BehaviorHaulWhenFull Haul { get; init; }

        /// <summary>Держать точку возрождения дома: возрождаться там, где лежат запасы.</summary>
        public required BehaviorKeepHomeSpawn HomeSpawn { get; init; }

        /// <summary>Приветствие игроков эмоцией и память встреч.</summary>
        public required BehaviorGreetPlayers Greet { get; init; }

        /// <summary>Фонарь в руке вне дела.</summary>
        public required BehaviorCarryLight CarryLight { get; init; }

        /// <summary>Что делать, когда дел не осталось (по умолчанию — стоять).</summary>
        public required BehaviorIdle Idle { get; init; }

        /// <summary>
        /// СТОЯТЬ В ОДНОЙ ТОЧКЕ И ВОЗВРАЩАТЬСЯ, ЕСЛИ УШЁЛ. Собирается У ВСЕХ и
        /// МОЛЧИТ, пока роль не задала точку (<see cref="BehaviorHoldPost.Post"/>
        /// пуст) — ровно как разгрузка, уход от нестабильности и поход за
        /// вещами: механизм лежит в наборе, включает его роль.
        ///
        /// Собирал его СТРАЖНИК, у себя в <c>Configure</c>, и приёмка назвала
        /// это верно: правило лежит в библиотеке, а дотянуться до него мог один
        /// стражник. Упираются в «стой тут» ещё и продавец у прилавка, и бот у
        /// печи.
        /// </summary>
        public required BehaviorHoldPost Post { get; init; }
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: за какой нуждой встают с кровати (и ради какой не
    /// ложатся вовсе). null — нужды нет, спать можно.
    ///
    /// Голод впереди раны нарочно: рана сама по себе не убивает, а голод
    /// убивает — и делает это, пока бот лежит и «спит до рассвета».
    /// Оба вопроса задаются в виде «нужда есть И есть чем её закрыть»: будить
    /// бота ради голода, который нечем утолить, значит менять сон на бессонницу.
    ///
    /// Ни мира, ни бота здесь нет — только два ответа «да/нет», поэтому правило
    /// проверяется стендом.
    /// </summary>
    public static string? WakeReason(bool hungryWithFood, bool hurtWithBandage) =>
        hungryWithFood ? "проголодался, а еда при себе"
        : hurtWithBandage ? "ранен, а бинт при себе"
        : null;

    /// <summary>
    /// Выживание «как у человека»: не висеть в воздухе, прятаться от бури,
    /// отбиваться, лечиться, есть, добывать еду, держать снаряжение под рукой,
    /// к ночи браться за оружие и выглядеть живым.
    ///
    /// Порядок = приоритет: буря важнее драки, драка важнее обеда.
    /// Все сообщения уходят в общий журнал бота.
    /// </summary>
    public static SurvivalKit Survive(this VsBot bot)
    {
        // ВОЗРОЖДЕНИЕ ВПЕРЕДИ ВСЕГО: мёртвому сервер отказывает во всём,
        // и любая другая способность будет впустую махать руками
        var respawn = new BehaviorRespawnWhenDead(bot.Context);
        respawn.OnDead += s => bot.Log($"я мёртв уже {s:0} с — встаю");
        respawn.OnAlive += () => bot.Log("снова на ногах");
        // «Жизни кончились» говорится ОДИН раз, а не каждые четыре секунды до
        // утра: сервер на запрос возрождения отвечает «Cannot revive!» и не
        // делает больше ничего — долбить кнопку бессмысленно
        respawn.OnNoLivesLeft += why => bot.Log(why);

        var grounded = new BehaviorStayGrounded(bot.Context);
        grounded.OnFell += h => bot.Log($"падал с {h:0.#} бл — под ногами было пусто");

        // УЙТИ ИЗ ОГНЯ — СРАЗУ ЗА ПАДЕНИЕМ. Сперва долететь до земли (в
        // воздухе бот всё равно не сдвинется с места), потом сойти с клетки,
        // которая жжёт. Всё остальное — еда, лечение, бой — ждёт: гореть и
        // драться одновременно значит сгореть победителем
        var leaveHarm = new BehaviorLeaveHarm(bot.Context);
        leaveHarm.OnLeaving += (where, what) =>
            bot.Log($"стою в огне ({what} в {where}) — ухожу");
        leaveHarm.OnLeft += (from, to) => bot.Log($"ушёл из огня: {from} → {to}");
        leaveHarm.OnStuckInHarm += m => bot.Log(m);
        leaveHarm.Пожар.OnClear += m => bot.Log(m);

        var storm = new BehaviorShelterFromStorm(bot.Context, bot.Shelter);
        storm.OnHiding += s => bot.Log($"временная буря ({s}) — прячусь");
        storm.OnStormOver += () => bot.Log("буря прошла — возвращаюсь к делам");
        // ЗАМУРОВАН И НЕ ВЫБРАЛСЯ — громко и один раз. Без этой подписки беда
        // 16.08 («после шторма застрял») осталась бы немой: сама способность
        // молчала, а укрытие печатало только безликое «стенка не поддалась»
        storm.Trapped.OnRaise += m => bot.Log(m);
        storm.Trapped.OnClear += m => bot.Log(m);

        // СУМКИ ПОЛНЫ — САМ ИДУ НА СКЛАД. Собирается ЗДЕСЬ, раньше своего места
        // в списке, ровно из-за следующей строки: рейс на склад и бой делят
        // одну ступень важности тела, и кто кому уступает — решение пресета, а
        // не самой способности. Выключена по умолчанию: решает роль
        var haul = new BehaviorHaulWhenFull(bot.Context) { Enabled = false };
        haul.OnGoing += free => bot.Log($"сумки полны (мест {free}) — иду на склад");
        // ОТЧЁТ О РЕЙСЕ ПЕЧАТАЕТ САМ СКЛАД («[склад] на склад сложено 112 шт.…»),
        // и второй строкой о том же журнал не заливаем: OnHauled оставлен ролям,
        // которым надо считать рейсы, а не пересказывать их
        haul.Trouble.OnRaise += m => bot.Log(m);
        haul.Trouble.OnClear += m => bot.Log(m);

        var combat = new BehaviorAttackHostiles(bot.Context);
        combat.OnEngage += e => bot.Log($"отбиваюсь от {e.Code}");
        // ТРИ ИСХОДА, ТРИ РАЗНЫХ СЛОВА. Прежде их было два, и «повержен»
        // печаталось в том числе когда цель просто пропала из мира: живьём это
        // дало сто восемьдесят «bowtornprojectile повержен» подряд (17.08,
        // 23:49:47 → 23:51:14) — над одной воткнутой стрелой, которую бот ни
        // разу не ударил
        combat.OnDisengage += (e, end) => bot.Log(end switch
        {
            CombatEnd.Defeated => $"{e.Code} повержен",
            CombatEnd.Vanished => $"{e.Code} пропал из виду — победы не считаю",
            _ => "противник ушёл"
        });
        combat.OnAttackerOutOfReach += (who, radius) =>
            bot.Log($"меня бьёт «{who}», а живого врага в {radius:0.#} бл нет — " +
                    "достать некого, ищу укрытие");
        // ПРИЧИНА УРОНА И РЕШЕНИЕ — ДВЕ СТРОКИ, ОБЕ С ЧИСЛАМИ.
        //
        // Именно этих двух строк не хватало в журнале заказчика: там было
        // «Потеряно 0,03 хп дрифтер (моб)» от сервера и молчание бота, а
        // дальше — семнадцать «тупик …» из движения. Теперь видно и от чего
        // урон, и что бот из этого вывел, и почему выбрал именно это
        combat.OnHurtCause += (hit, verdict) =>
            bot.Log(CombatEntry.Say(verdict, hit, combat.MinHurtToFight));
        combat.OnPlan += (enemy, plan) => bot.Log($"{enemy.Code} в бою — {plan}");
        combat.OnStrangeAttacker += (who, nearest) =>
            bot.Log($"сервер говорит, ударил «{who}», а рядом такого нет; ближе всех " +
                    $"{nearest}, но он меня не трогал — бить не того я не стану");
        combat.OnRevengeTarget += code => bot.Log($"меня ударил {code} — иду разбираться");
        combat.OnRevengeGaveUp += code => bot.Log($"{code} не догнать — возвращаюсь к делам");
        combat.OnRetreat += f => bot.Log($"бой не вытяну ({f:P0} здоровья) — отступаю");
        // БРОНЯ БОЛЬШЕ НЕ МЕНЯЕТСЯ МОЛЧА. В живом журнале 16.08 про неё нет ни
        // строки — событие было, а подписчика не было ни одного, и «почему бот
        // ползает и жрёт вдвое больше» человеку взять было неоткуда
        combat.OnArmorSay += m => bot.Log($"[броня] {m}");
        combat.OnSay += m => bot.Log($"[бой] {m}");

        // ЛУК ГОВОРИТ ВСЛУХ И ПРО ВЫСТРЕЛ, И ПРО ОТКАЗ. Без второй подписки
        // выходит ровно та беда, ради которой закон и требует честных отказов:
        // без стрел игра НИЧЕГО не отвечает (сервер даже не открывает
        // удержание), и «бот не стреляет» выглядело бы поломкой лука, а не
        // пустым колчаном. Заглушку от повторов держит сам механизм — отказ
        // это состояние, и пока между ботом и дрифтером стена, он один и тот же
        combat.OnShot += (кто, бл) => bot.Log($"[лук] выстрелил в {кто} ({бл:0.#} бл)");
        combat.Bow.OnLog += m => bot.Log($"[лук] {m}");

        // БЕГСТВО — ровно две строки на побег, обе с числами. Прежняя подписка
        // (OnFlee, «драться нечем — убегаю от X») отсюда убрана: она врала при
        // отступлении по здоровью (оружие-то было) и дублировала бы начало
        // побега вторым сообщением
        combat.Refuge = bot.Shelter;   // чем закладываться, когда бежать некуда
        combat.OnFleeFrom += s => bot.Log(FleeRules.Say(s));
        combat.OnFled += r => bot.Log(FleeRules.Say(r));
        // «ВИЖУ, НО НЕ ЛЕЗУ» ЗДЕСЬ БОЛЬШЕ НЕ ПЕЧАТАЕТСЯ, и это нарочно. Ровно
        // то же самое говорит строка решения (OnPlan) — и говорит подробнее: не
        // только «далеко», но и почему не выйдет ни догнать, ни уйти, ни
        // закрыться. Две строки об одном и есть тот шум, за которым в журнале
        // заказчика не было видно беды. Само событие оставлено РОЛЯМ: кому-то
        // «рядом бродит тварь» — повод сменить место работы, а не строка в лог
        // БОЙ ВАЖНЕЕ РЕЙСА НА СКЛАД, И ЭТО РЕШАЕТСЯ ЗДЕСЬ.
        //
        // Рейс держит тело как рефлекс — иначе прерванный карьер счёл бы уход
        // на склад сменой цели и не вернулся бы (BodyArbiter.Preemption). Но
        // равный равного не перебивает, и бой, попросив тело посреди рейса,
        // получал бы честный отказ всю дорогу до сундука: бот шёл бы с грузом
        // под ударами дрифтера и не отбивался. Отказывать бою нельзя, понижать
        // важность рейса — значит терять возврат к работе; поэтому рейс
        // уступает сам, по просьбе
        combat.OnBodyBusy += who =>
        {
            if (haul.GiveWay(who))
            {
                bot.Log("бой важнее рейса на склад — схожу с тела, вернусь к складу после стычки");
                return;
            }
            bot.Log($"в бой не лезу: телом распоряжается «{who}»");
        };
        combat.OnCornered += () => bot.Log("бежать некуда");
        combat.OnFleeUnarmed += (code, power) =>
            bot.Log($"драться нечем: лучшее в хотбаре — {code} ({power:0.#})");
        combat.OnReady += f => bot.Log($"отлежался ({f:P0}) — снова принимаю бой");

        var healing = new BehaviorHealWhenHurt(bot.Context) { IsHostile = combat.IsHostile };
        // «ПО РЕЕСТРУ» — НЕ УКРАШЕНИЕ. Здоровье в игре подтягивается ещё
        // секунд десять после применения, и число тут — обещание реестра, а не
        // прибавка, которую бот увидел. Само же слово «использовал» теперь
        // говорится ТОЛЬКО когда сервер списал бинт из сумки
        healing.OnHealed += (code, hp) => bot.Log($"использовал {code} (по реестру +{hp:0.#} хп)");
        healing.OnNoHealingItem += () => bot.Log("ранен, а лечиться нечем — смотрю, где взять");
        healing.OnHealFailed += (code, why) => bot.Log($"не смог полечиться {code}: {why}");
        // ЧЕМ КОНЧИЛСЯ ПОХОД ЗА БИНТОМ — ОБЯЗАТЕЛЬНО ВСЛУХ. Прежде здесь было
        // одно «ранен, а лечиться нечем», и по нему нельзя было понять,
        // смотрел бот в сундук или нет; цепочка снабжения называет каждое
        // оборвавшееся звено своими словами, и это ровно то, чего не хватало
        healing.OnRestocked += why => bot.Log($"[лечебное] {why}");

        var eating = new BehaviorEatWhenHungry(bot.Context);
        eating.OnAte += (code, _) => bot.Log($"поел {code}");
        eating.OnNoFood += () => bot.Log("проголодался, а еды нет");
        // «—» вместо кода означает «беда не про конкретную еду» (сытость
        // неизвестна, сервер морит голодом): строка «не смог поесть —: …»
        // читалась как поломка журнала
        eating.OnEatFailed += (code, why) => bot.Log(code == "—"
            ? $"поесть не выходит: {why}"
            : $"не смог поесть {code}: {why}");
        eating.OnGoingForDrop += d => bot.Log($"еда лежит в {d:0.#} бл — иду поднимать");

        // ОХОТА. Числа замаха и длины руки НЕ СВОИ: хозяин у них бой, и вторая
        // копия однажды разошлась бы с первой молча. «Рядом опасно» тоже
        // спрашивается у боя — тем же способом, что и у сна ниже
        var hunting = new Hunting(bot.Context, bot.Hands, bot.Butchering)
        {
            AttackReach = combat.AttackReach,
            SwingIntervalMs = combat.SwingIntervalMs,
            Danger = combat.IsHostile
        };
        hunting.OnStalking += (кто, бл) => bot.Log($"[охота] иду на {кто}: {бл:0.#} бл");
        hunting.OnKilled += кто => bot.Log($"[охота] {кто} повержен");
        // ОХОТИТЬСЯ НЕ НА КОГО — громко один раз, а не сто строк в минуту
        hunting.НеНаКого.OnRaise += m => bot.Log($"[{m}]");
        hunting.НеНаКого.OnClear += m => bot.Log($"[{m}]");

        var foraging = new BehaviorForageWhenNoFood(bot.Context, bot.Foraging, bot.Cooking, hunting);
        foraging.OnGathered += n => bot.Log($"набрал дикой еды в {n} местах");
        // ЕДА ИЗ КЛАДОВЫХ РАНЬШЕ КУСТОВ — и о том, чем это кончилось, тоже
        // молчать нельзя: «сходил домой и не нашёл» и «домой не ходил» человек
        // должен различать, иначе он понесёт боту хлеб, который у бота есть
        foraging.OnFromStores += why => bot.Log($"[еда] {why}");
        // ЧЕМ КОНЧИЛСЯ ПОХОД ЗА ЕДОЙ — ЦЕЛИКОМ, СО ВСЕМИ ЗВЕНЬЯМИ. Ровно этого
        // не хватало 26.08: цепочка обрывалась на «дикой еды в 32 бл не видно»,
        // и по журналу нельзя было понять, пробовал ли бот готовить и охотиться
        foraging.OnQuest += отчёт => bot.Log($"[еда] {отчёт.Reason}");

        var gear = new BehaviorKeepGearReady(bot.Context);
        gear.OnMovedToHotbar += (code, slot) => bot.Log($"убрал в хотбар (слот {slot}): {code}");

        var night = new BehaviorWeaponAtNight(bot.Context);
        night.OnArmed += (code, power) => bot.Log($"к ночи взял {code} (боевая ценность {power:0.#})");
        night.OnNoWeapon += () => bot.Log("ночь на носу, а оружия нет");

        var alive = new BehaviorLookAlive(bot.Context);

        // Сон идёт сразу после гравитации: лежачего бота не дёргают делами,
        // но «не висеть в воздухе» важнее — падающему спать негде
        var sleep = new BehaviorSleepAtNight(bot.Context, bot.Sleeping) { IsDangerous = combat.IsHostile };
        // ДОМ У БОТА ОДИН, и сон спрашивает ЕГО ЖЕ — функцией, как и отметка
        // точки возрождения ниже. Ручка роли «Дом» обещает человеку «где спать»,
        // и до этой строки обещание было пустым: сон смотрел только под ноги
        sleep.Home = () => bot.Shelter.Home;
        sleep.OnWentToBed += bed => bot.Log($"лёг спать ({bot.PlayerCoords(bed.Pos.X, bed.Pos.Y, bed.Pos.Z)})");
        sleep.OnCouldNotSleep += why => bot.Log($"поспать не вышло: {why}");
        sleep.OnWokeUp += why => bot.Log($"встал: {why}");
        // ЧЕГО НЕ ХВАТАЕТ, ЧТОБЫ ЛЕЧЬ. Живой прогон 16.08: за ночь про сон в
        // журнале не было НИ ОДНОЙ строки, а заказчик написал «не ложится спать»
        sleep.OnNotSleeping += why => bot.Log($"[сон] {why}");

        // ГОЛОД И РАНА ПОДНИМАЮТ С КРОВАТИ. Пока этой строки не было, у пути
        // «голоден → поел» стоял замок, о котором никто не думал: раннер
        // обрывает обход на первой способности, вернувшей true, сон весит 90 и
        // держит ход до рассвета — тик еды (80) не звался ВООБЩЕ. А сон в игре
        // гонит время, и сытость за ночь падает до нуля. Бот вставал только
        // когда голод начинал бить уроном, потому что урон поднимает Danger().
        //
        // Спрашиваем «нужда есть И есть чем её закрыть»: будить бота ради
        // голода, который нечем утолить, значит менять сон на бессонницу
        sleep.WakeFor = () => WakeReason(eating.NeedsFood && eating.HasFood, healing.NeedsHealing);

        var warm = new BehaviorKeepWarm(bot.Context, bot.Cold, bot.Fire);
        // ГРАДУСОВ ЗДЕСЬ НЕТ И НЕ БЫЛО. OnFreezing даёт ДОЛЮ замерзания 0..1
        // (freezingEffectStrength — та самая, по которой игра решает, что тело
        // мёрзнет), а журнал печатал её со значком градуса: «мёрзну (0,1°)»
        // читается как «остыл на десятую долю градуса», то есть «пустяк», тогда
        // как на деле это уже «мёрзнет» и −0,4 °C от порога обморожения. Печатаем
        // процентами и добавляем сами градусы — ими человек и меряет холод
        // Градусы ВИДИМЫЕ (BodyHeat.TemperatureShown) — те же, что игра пишет
        // на экране персонажа: журнал человек сверяет с игрой, а не с атрибутами
        warm.OnFreezing += t => bot.Log(
            $"мёрзну на {t:P0} (тело {(bot.Cold.TemperatureShown is { } гр ? $"{гр:0.#} °C" : "сколько — не знаю")}) — " +
            "одеваюсь и ищу тепло");
        warm.OnWarm += () => bot.Log("согрелся");
        // СОГРЕТЬСЯ НЕ ВЫШЛО — И ОБ ЭТОМ ГОВОРЯТ. Три честных отказа («костра
        // нет и развести не вышло», «костёр … не горит», «дошёл, но тепла не
        // чувствую») были в дереве и не доходили до человека НИ РАЗУ: события
        // не слушал никто. Мёрзнущий на 100 % бот молча ходил кругами — ровно
        // то молчание, которое правило 4 и запрещает.
        //
        // Заглушки повторов здесь нет и не нужно: способность говорит это один
        // раз на повод сама (BehaviorKeepWarm.безОгня — общий Alarm), поэтому
        // подписка прямая, как у всех соседей
        warm.OnNoFire += why => bot.Log($"[тепло] {why}");

        // УХОД ОТ НЕСТАБИЛЬНОСТИ — выключен по умолчанию: бежать или терпеть,
        // решает роль. Все четыре строки с числами: без них «ушёл наверх» не
        // отвечает на единственный важный вопрос — стало ли лучше
        var stability = new BehaviorSeekStability(bot.Context) { Enabled = false };
        stability.OnRunning += why => bot.Log($"ухожу из нестабильного места: {why}");
        stability.OnLeavingRift += (rift, dist) =>
            bot.Log($"рядом {rift}, до него {dist:0.#} бл — он и тянет стабильность, отхожу");
        stability.OnClimbing += (where, up) =>
            bot.Log($"поднимаюсь на {up} бл ({bot.PlayerCoords(where.X, where.Y, where.Z)}) — " +
                    "выше стабильность места не ниже, это единственное, что известно без сида мира");
        stability.OnNowhereToGo += why => bot.Log($"[стабильность] {why}");
        stability.OnResult += what => bot.Log($"[стабильность] {what}");
        stability.OnCalm += why => bot.Log($"[стабильность] {why}");

        // Возврат за вещами — ВЫКЛЮЧЕН по умолчанию: идти на место смерти или
        // нет, решает роль (курьеру надо, лавочнику посреди торговли — нет)
        var recover = new BehaviorRecoverLoot(bot.Context, bot.Deaths) { Enabled = false };
        recover.OnHeadingToGrave += (p, dist) =>
            bot.Log($"иду за вещами на {bot.PlayerCoords(p.X, p.Y, p.Z)} ({dist:0} бл)");
        recover.OnRecovered += n => bot.Log($"подобрал вещей: {n}");
        recover.OnGaveUp += why => bot.Log($"за вещами не иду: {why}");

        // ДОМ КАК ТОЧКА ВОЗРОЖДЕНИЯ. Возродившийся у мирового спавна бот в
        // четырёхстах блоках от своих вещей — это потерянный вечер. Дом берём
        // ФУНКЦИЕЙ у укрытия: точка дома в боте одна, вторая копия однажды
        // разъехалась бы с панелью
        var homeSpawn = new BehaviorKeepHomeSpawn(bot.Context);
        homeSpawn.Spawn.Home = () => bot.Shelter.Home;
        homeSpawn.Spawn.OnLog += m => bot.Log($"[дом] {m}");
        homeSpawn.OnMarked += r => bot.Log($"[дом] {r}");
        homeSpawn.OnAtHome += why => bot.Log($"[дом] {why}");
        homeSpawn.OnCannotMark += why => bot.Log($"[дом] {why}");

        // Замену инструмента ставим ДО «снаряжения под рукой»: иначе они
        // каждый тик перекладывают предметы друг за другом
        var wornTool = new BehaviorSwapWornTool(bot.Context, bot.Wear);
        wornTool.OnToolWorn += (code, left) => bot.Log($"{code} почти сломан (осталось {left}) — меняю");

        var lit = new BehaviorKeepLit(bot.Context, bot.Lighting);
        lit.OnTorchPlaced += p => bot.Log($"поставил факел ({bot.PlayerCoords(p.X, p.Y, p.Z)})");

        // ДОДЕЛАТЬ НАЧАТОЕ — ВЫКЛЮЧЕНО по умолчанию: решает роль. Продавцу за
        // прилавком собирать нож из лезвия незачем, копателю без топора не
        // работать вовсе. Живой случай — журнал 16.08: вытесанное
        // knifeblade-peridotite пролежало в сумке весь вечер, потому что
        // спросить про него было некому
        var finish = new BehaviorFinishStarted(bot.Context) { Enabled = false };
        finish.OnFinished += what => bot.Log($"[начатое] {what}");
        finish.OnRefused += why => bot.Log($"[начатое] {why}");

        // СНАРЯДИТЬСЯ — ТОЖЕ ВЫКЛЮЧЕНО по умолчанию и тоже решает роль.
        // В отличие от доделки эта ХОДИТ: кирка лежит в сундуке склада, и
        // «не пойду» означает «останусь без кирки навсегда». Живой случай
        // 23.08: бот перечислил, что у него нет ни кирки, ни ножа, ни лестниц,
        // сказал «в простое я за этим не хожу» — и умер дважды за девять минут
        var equip = new BehaviorEquipSelf(bot.Context) { Enabled = false };
        equip.OnEquipped += what => bot.Log($"[снаряжение] {what}");
        equip.OnRefused += why => bot.Log($"[снаряжение] {why}");

        // Своя добыча: сломал блок — вещь упала на землю и ждёт, пока по ней
        // пройдут ногами. Стоит ПОСЛЕ еды (голодному не до камней) и ПЕРЕД
        // собирательством: сперва забрать своё, потом искать чужое
        var drops = new BehaviorPickUpDrops(bot.Context);
        drops.OnCollected += r => bot.Log($"добыча: {r}");
        // КУДА И ЗАЧЕМ УШЁЛ — ДО ПЕРВОГО ШАГА. Без этой строки бот, только что
        // доложивший «я дома», через секунду молча уходил за тридцать блоков, и
        // человек читал это как поломку (журнал 17.08, 23:54–23:57)
        drops.OnGoing += (где, сколько) => bot.Log(
            $"[своя добыча] иду к помеченному месту {bot.PlayerCoords(где.X, где.Y, где.Z)}: {сколько:0.#} бл");
        drops.OnRefused += why => bot.Log($"[своя добыча] {why}");

        // ДОБЫЧА С УБИТЫХ — СРАЗУ ЗА СВОЕЙ ДОБЫЧЕЙ. Порядок тот же, что и
        // смысл: сперва поднять то, что сам выломал, потом обобрать убитого.
        // Живой журнал 16.08 показывал двенадцать «drifter-normal повержен» за
        // смену и ни одной строки о добыче — способности, которая скажет
        // подбору и разделке «а теперь заберите», просто не существовало
        var kills = new BehaviorLootKills(bot.Context, bot.Butchering);
        // ОДНА СТРОКА НА ТЕЛО — и ещё одна, если решение по нему ПЕРЕМЕНИЛОСЬ
        // (нашёлся нож, бот подошёл ближе предела). Заглушку повторов держит
        // сама способность, здесь остаётся только напечатать
        kills.OnBody += (code, выбор) => bot.Log($"[с убитых] {code}: {выбор.Почему}");
        // КУДА УШЁЛ — ДО ПЕРВОГО ШАГА. Без этой строки бот, доложивший «я дома»,
        // через три секунды молча уходил к туше за одиннадцать блоков, и понять
        // это можно было только сверив координаты руками (журнал 18.08, 00:17)
        kills.OnGoing += (туш, бл) => bot.Log(
            $"[с убитых] иду разделывать: туш {туш}, ближайшая в {бл:0.#} бл");
        kills.OnButchered += r => bot.Log($"[с убитых] {r}");
        kills.OnRefused += why => bot.Log($"[с убитых] {why}");
        // БРОСИЛ РАБОТУ РАДИ ТУШИ — со сроком и с именем брошенного дела.
        // Распорядитель тела печатает своё «важнее — прерываю», но числа, ради
        // которого размен и делается, у него нет: три игровых часа туши
        // дрифтера видит только разделка
        kills.OnPreempted += why => bot.Log($"[с убитых] {why}");

        // Приветствие раньше фонаря: человек стоит перед носом и ждёт, а
        // фонарь подождёт полсекунды. Обе — после дел, но перед косметикой
        var greet = new BehaviorGreetPlayers(bot.Context, bot.Emotes, bot.Meetings, bot.MeetingsFile);
        greet.OnGreeted += (who, how) => bot.Log($"поздоровался с {who} ({how})");
        greet.OnMet += (m, fresh) => { if (fresh) bot.Log($"вижу {m.Name}, встреча {m.Times}-я"); };
        greet.OnGreetFailed += (who, why) => bot.Log($"с {who} не поздоровался: {why}");
        greet.OnForgot += n => bot.Log($"забыл давних знакомых: {n}");

        // Механизм левой руки берём У БОТА, а не заводим свой: тот же самый
        // спрашивает окно управления («взять в левую руку»), и две копии
        // разошлись бы в первый же день
        var carryLight = new BehaviorCarryLight(bot.Context, bot.Light);
        carryLight.Light.OnLog += m => bot.Log($"[свет в руке] {m}");
        carryLight.OnLightInHand += (code, where) => bot.Log($"взял {code} в руку ({where})");
        carryLight.OnPutAway += (code, why) => bot.Log($"убрал {code} из левой руки: {why}");
        carryLight.OnNothingToLight += () => bot.Log("темно, а светить нечем");
        carryLight.OnSkipped += why => bot.Log($"фонарь в руку не беру: {why}");

        // УДЕРЖАНИЕ ТОЧКИ. Пост пуст — способность молчит и ведёт себя ровно
        // как её отсутствие; точку даёт роль (стражнику — своей ручкой «Пост»).
        //
        // МЕСТО В СПИСКЕ — ПОСЛЕ ПОДБОРА ДОБЫЧИ, ДОБЫЧИ С УБИТЫХ, РАЗГРУЗКИ И
        // СБОРА ЕДЫ, и это ровно то, чего мы хотим: сперва доделать дело, ради
        // которого сошёл с поста, потом вернуться. Порядок обхода решает ВЕС, а
        // при равном весе — место в этом списке; поставь мы удержание раньше,
        // бот шёл бы на пост, бросив на земле только что выломанное.
        // Отказ называет ВИД беды, а имя ручки дописывает роль: у стражника
        // поводок зовётся «ВозвращатьсяНаПостНеДальше», у продавца —
        // «ПоводокОтПоста», и назови библиотека одно из имён, второй потребитель
        // получил бы отказ с чужой ручкой
        var post = new BehaviorHoldPost(bot.Context);
        post.OnGoingBack += (куда, отступ) =>
            bot.Log($"[пост] отошёл на {отступ:0.#} бл — возвращаюсь " +
                    $"({bot.PlayerCoords(куда.X, куда.Y, куда.Z)})");
        post.OnStood += куда =>
            bot.Log($"[пост] на посту ({bot.PlayerCoords(куда.X, куда.Y, куда.Z)})");

        // БЕЗ ДЕЛА — ПОСЛЕДНЕЙ В СПИСКЕ: пока способность бродит или идёт
        // домой, она держит ход, и всё, что стоит за ней, в этот тик не тикает
        var idle = new BehaviorIdle(bot.Context, bot.Shelter);
        idle.OnStanding += s => bot.Log($"без дела {s:0} с — стою, так настроено");
        idle.OnWander += p => bot.Log($"без дела — пройдусь до ({bot.PlayerCoords(p.X, p.Y, p.Z)})");
        idle.OnGoingHome += p => bot.Log($"без дела — иду домой ({bot.PlayerCoords(p.X, p.Y, p.Z)})");
        idle.OnCameHome += () => bot.Log("я дома");
        idle.OnRefused += why => bot.Log($"[безделье] {why}");
        idle.OnLeaving += why => bot.Log($"выхожу из игры: {why}");
        // ЧТО ИМЕННО ДОДЕЛЫВАЮ И ПОЧЕМУ — вслух и до работы. Заказчик просил
        // ровно этого: бот, молча ушедший доделывать, со стороны неотличим от
        // бота, который ушёл гулять
        idle.OnFinishing += end => bot.Log(Unfinished.Say(end));
        idle.OnFinished += m => bot.Log($"[доделал] {m}");
        idle.OnJobs += m => bot.Log($"[цикл задач] {m}");

        // ПОРЯДОК = ПРИОРИТЕТ. Дорога домой ради точки возрождения стоит после
        // похода за вещами: и то и другое — дальняя дорога, но вещи тухнут по
        // часам, а точка ждёт. «Выглядеть живым» и «без дела» — в самом
        // хвосте: они не должны перебивать ни одного настоящего дела
        //
        // РАЗГРУЗКА — ПОСЛЕ СВОЕЙ ДОБЫЧИ И ПЕРЕД СБОРОМ ЯГОД. Сперва поднять с
        // земли то, что сам же выломал (иначе оно так и останется лежать),
        // потом отнести всё разом на склад, а за дикой едой идти уже после: в
        // полную сумку ягоды всё равно не влезут
        // УХОД ОТ НЕСТАБИЛЬНОСТИ СТОИТ РЯДОМ С ТЕПЛОМ, СРАЗУ ЗА НИМ: обе беды
        // одного рода — «не убьёт сейчас, но убьёт, если стоять». В бурю
        // способность сама уступает укрытию (оно выше и по весу, и по смыслу)
        foreach (BotBehavior b in new BotBehavior[]
                 { respawn, grounded, leaveHarm, sleep, storm, combat, healing, eating, warm, stability,
                   recover, drops, kills, haul, foraging, post, equip, homeSpawn, wornTool,
                   gear, night, lit, finish, greet, carryLight, alive, idle })
            bot.Behaviors.Add(b);

        // ТЕЛА НЕ ДАЛИ ДЕЛУ ЖИЗНИ — это обязано быть слышно ВСЕГДА и у любой
        // роли, а не только у той, что не забыла подписаться. Обычные отказы
        // распорядитель глушит на три секунды, этот — никогда: молчаливый
        // отказ поесть и есть та самая смерть с хлебом в кармане
        bot.Context.Turn.OnVitalRefused += why => bot.Log($"[жизнь] {why}");

        return new SurvivalKit
        {
            Respawn = respawn, Grounded = grounded, LeaveHarm = leaveHarm,
            Storm = storm, Combat = combat, Healing = healing,
            Eating = eating, Foraging = foraging, Hunting = hunting, Gear = gear,
            NightWeapon = night, Alive = alive,
            Sleep = sleep, Warm = warm, Stability = stability,
            WornTool = wornTool, Recover = recover, Lit = lit, Finish = finish,
            Equip = equip,
            Drops = drops, Kills = kills, Haul = haul, HomeSpawn = homeSpawn, Greet = greet,
            CarryLight = carryLight, Idle = idle, Post = post
        };
    }

    /// <summary>
    /// ОТВЕТ КОМАНДЫ «!СТАБИЛЬНОСТЬ» — целиком, одной строкой.
    ///
    /// Вынесен из тела команды НАРОЧНО, и вот почему. Ответ уходит в игровой
    /// чат (<c>ReplyAsync</c>), а <c>ChatCommands.RunAsync</c> возвращает
    /// позвавшему лишь слово «выполнено» — то есть СОДЕРЖИМОГО ответа не видел
    /// никто, включая приёмку: тест «команда отвечает и про число, и про
    /// разломы» проходил бы и на команде, которая падает при каждом вызове.
    /// Теперь строку, которую прочитает человек, можно спросить и проверить —
    /// ту же самую, что уходит в чат, а не её двойника.
    ///
    /// Здесь только СБОР ЧИСЕЛ у бота; что из них сказать, решают чистые
    /// правила (<see cref="StabilityRules.Say"/> и
    /// <see cref="StabilityRules.SayRifts"/>).
    /// </summary>
    public static string StabilityAnswer(VsBot bot)
    {
        var s = bot.Stability;
        var p = bot.Self.Position;
        string разломы = StabilityRules.SayRifts(s.RiftsEnabled, s.RiftsMode,
            s.Nearest(p?.X, p?.Y, p?.Z));
        string мир = s.Own == null && s.OnInThisWorld == false
            ? " Настройки мира говорят, что временной стабильности тут нет вовсе."
            : "";
        return $"Стабильность: {s.Say()}.{мир} {разломы}";
    }

    /// <summary>
    /// Обычные команды чата, которые ждут от любого бота: !пинг, !хп, !инв,
    /// !кто, !иди, !комне, !стоп, !время. Роль добавляет к ним свои.
    /// </summary>
    public static VsBot CommonCommands(this VsBot bot)
    {
        bot.Commands.Add("пинг", "проверка связи", a => a.ReplyAsync("понг!"));

        // «ПОПРОБУЙ ЕЩЁ РАЗ» — одной командой, без перезапуска программы.
        //
        // Дорожная память нарочно живёт минутами: тупики дорожают на десятки
        // секунд, запертая приватом дверь выпадает из маршрутов на две минуты.
        // Но мир меняется не по нашим часам — хозяин добавил бота в заявку,
        // разобрал завал, открыл ворота. Ждать в такой момент нечего, а
        // единственным способом стереть память был самоперезапуск, до которого
        // бот доходит, только пока не сдастся сам
        bot.Commands.Add("забудь", "забыть тупики, запертые двери и приметы чужого — пробовать заново",
            a =>
        {
            bot.Movement.Forget();
            // ТА ЖЕ БЕДА С ДРУГОЙ СТОРОНЫ. «Тут сервер не дал ломать» бот
            // помнит полчаса — и всё это время обходит место, куда хозяин уже
            // впустил его поимённо. Заявки сервера при этом остаются: их
            // держит сервер и сам присылает отмену, забывать их нам нечем
            int чужое = bot.Claims.Forget();
            return a.ReplyAsync("Забыл дорожную память: тупики и запертые двери" +
                                (чужое > 0 ? $"; и примет чужого жилья: {чужое}" : "") +
                                ". Пробую заново");
        });

        bot.Commands.Add("хп", "здоровье, сытость, позиция", a => a.ReplyAsync(
            $"Здоровье {bot.Self.Health:0.#}/{bot.Self.MaxHealth:0.#}, " +
            $"сытость {bot.Self.Saturation:0.#}/{bot.Self.MaxSaturation:0.#}, " +
            $"позиция ({bot.PlayerCoordsHere()})" + (bot.Movement.IsSwimming ? ", плыву" : "")));

        bot.Commands.Add("время", "который час и что с бурями", a => a.ReplyAsync(
            $"{bot.Clock}" +
            (bot.Storms.Known
                ? bot.Storms.Active
                    ? $"; идёт буря ({bot.Storms.Strength})"
                    : $"; буря ({bot.Storms.Strength}) через {bot.Storms.DaysUntilStorm:0.#} сут"
                : "; про бури ничего не знаю")));

        // СТАБИЛЬНОСТЬ — ОТДЕЛЬНОЙ КОМАНДОЙ, а не строкой в «!хп»: в ней три
        // разных ответа (своё число, ход и разломы вокруг), и любой из них
        // может быть «не знаю» — а такое честное незнание надо уметь показать
        bot.Commands.Add("стабильность", "темпоральная стабильность и разломы вокруг",
            a => a.ReplyAsync(StabilityAnswer(bot)));

        bot.Commands.Add("инв", "что у меня в хотбаре", a =>
        {
            var hotbar = bot.Self.GetInventory("hotbar");
            string items = hotbar == null
                ? "нет данных"
                : string.Join(", ", hotbar.Where(s => !s.IsEmpty).DefaultIfEmpty(new SlotContent("пусто", 0)));
            return a.ReplyAsync($"Хотбар: {items}");
        });

        bot.Commands.Add("кто", "кого видно вокруг", a =>
        {
            var players = bot.Entities.All
                .Where(e => e.IsPlayer && e.Id != bot.Entities.OwnEntityId)
                .Select(e => e.ToString());
            string list = string.Join("; ", players);
            return a.ReplyAsync($"Сущностей вокруг {bot.Entities.Count}. Игроки: {(list.Length > 0 ? list : "нет")}");
        });

        bot.Commands.Add("иди", "иди <x> <z> или <x> <y> <z> — дойти до точки", async a =>
        {
            if (a.TryGetCell(out var cell))
            {
                await a.ReplyAsync($"Иду к ({a.Here(cell.X, cell.Y, cell.Z)})");
                // ПО ТОКЕНУ ВЛАДЕНИЯ, как и соседняя половина этой же команды
                // ниже. Тело «!иди» держит приказом (ChatCommands: команда
                // берёт его ступенью Command и кладёт токен в a.Token), а
                // дорогу звал без него — и, перебитый рефлексом, поход топал
                // свои пять минут уже чужим телом
                bool reached = await bot.Movement.TravelToAsync(cell, 300, a.Token);
                await a.ReplyAsync(reached ? $"Пришёл ({bot.PlayerCoordsHere()})"
                                           : $"Не дошёл, стою на ({bot.PlayerCoordsHere()})");
                return;
            }
            if (!a.TryGetCoords(out double x, out double z))
            {
                await a.ReplyAsync("Нужно: !иди <x> <z> или !иди <x> <y> <z> — координаты как в игре");
                return;
            }
            await a.ReplyAsync($"Иду к ({a.Here(x, bot.Self.Position?.Y ?? 0, z)})");
            // Тем же ходом, что и «комне»: упорной погоней с перепланировкой.
            // Высоту берём из мира — иначе цель уезжает на высоту, где бот
            // сейчас стоит. Заказчик заметил разницу верно: «комне» доходил
            // там, где «иди» сдавалось, — и дело было не в уме, а в упорстве
            int gx = (int)Math.Floor(x), gz = (int)Math.Floor(z);
            int gy = bot.World.NearestSupportY(gx, (int)Math.Floor(bot.Self.Position?.Y ?? 0), gz,
                up: 24, down: 24) ?? (int)Math.Floor(bot.Self.Position?.Y ?? 0);
            bool ok = await bot.Movement.TravelToAsync(new BlockPos(gx, gy, gz), 300, a.Token);
            await a.ReplyAsync(ok ? $"Пришёл ({bot.PlayerCoordsHere()})" : "Не дошёл — на пути стена или обрыв");
        });

        bot.Commands.Add("комне", "подойти к позвавшему", async a =>
        {
            if (a.SenderEntity is not { } caller)
            {
                await a.ReplyAsync("Не вижу тебя — подойди поближе");
                return;
            }
            // И ЭТОТ ПОХОД — ПО ТОКЕНУ ВЛАДЕНИЯ. Та же порода, что была у «!иди»
            // выше: тело команда держит приказом, а подход звала без него —
            // перебитый рефлексом, он гнался за человеком ещё полторы минуты
            bool ok = await bot.Movement.ApproachAsync(
                () => bot.Entities.Get(caller.Id) is { } t ? (t.X, t.Y, t.Z) : null,
                stopDistance: 1.5, maxSeconds: 90, ct: a.Token);

            // Что делать, если позвали НАВЕРХ и дороги туда нет, решает РОЛЬ:
            // одному боту уместно достроить столб, другому — честно сказать
            // «не дойду». Библиотека даёт механику (Climbing), а не решение
            if (bot.Entities.Get(caller.Id) is { } now)
                await bot.Movement.FaceAsync(now.X, now.Z);
            await a.ReplyAsync(ok ? $"Я тут! ({bot.PlayerCoordsHere()})"
                                  : $"Не смог дойти, стою на ({bot.PlayerCoordsHere()})");
        });

        // ТЕЛА НЕ ПРОСИМ (needsBody: false). Раньше «!стоп» брал тело
        // важностью «команда» — а его уже держала «команда карьер», равный
        // равного не перебивает, и вместо остановки человек получал «Сейчас
        // занят: команда карьер. Повтори позже». Остановить бота из чата было
        // нельзя ровно тогда, когда это и нужно.
        //
        // Гасить и навык, и возврат умеет один CancelCurrent: между заходами
        // работа тела не держит, прерывать нечего — а вернуться она всё равно
        // собиралась (см. SkillRunner.CancelCurrent)
        bot.Commands.Add("стоп", "прервать текущее дело", a =>
        {
            bot.Skills.CancelCurrent("человек сказал «стоп»");
            return a.ReplyAsync("Остановился");
        }, needsBody: false);

        return bot;
    }
}
