# Предметы Following Seas: технический справочник

Дата: 2026-10-08. Steam build **25792767**, Unity **6000.3.20f1**.
Baseline мода: plugin **0.1.0**, **Protocol 4**. Этот документ не реализует синхронизацию.

## 1. Источники, охват и ограничения

**Код** ниже означает декомпиляцию `Assembly-CSharp.dll` в
`B:\FollowingSeasModTest\_decompiled\build-25792767\`. Ссылки вида
`ObjectInteractionHandler.cs:273` относятся к файлам этого каталога, не к исходникам мода.
Чужой код в репозиторий не скопирован.

**Дамп H**: `B:\FollowingSeasModTest\BepInEx\FollowingSeasCoop-items-20261008-153718-8732495-Host.txt`.
**Дамп C**: `B:\FollowingSeasModTest\BepInEx\FollowingSeasCoop-items-20261008-153722-2508293-Client.txt`.
Это снимки работающей игры, предоставленные владельцем, а не выполненный автором документа
новый игровой прогон. Они подтверждают состав загруженного мира и Inspector-ссылки в нём,
но не доказывают выполнение каждого действия.

Справочник охватывает обнаруженные механизмы предметов: переносимые ресурсы, личные инструменты,
товарный груз, два пути рыбалки, добычу животных и смежные стационарные взаимодействия.
**Это не исчерпывающий список всех ассетов игры.** Код не содержит сериализованных сцен и
префабов. Два снимка с `gameplay` и `tiny_greenland_data` не перечисляют остальные острова,
все виды рыбы, животных, декоративные объекты или варианты кораблей. Для полного asset-каталога
нужны дополнительные игровые выгрузки либо отдельное извлечение сцен/префабов.

Навигация:
- [Классификация](#2-классификация)
- [Переносимые вещи](#3-переносимые-вещи-objectinteractionhandler)
- [Ресурсы](#4-ресурсы-корабля-provisionscontroller)
- [Инструменты](#5-личные-инструменты)
- [Товарный груз](#6-товарный-груз)
- [Рыба и охота](#7-рыба-и-охота)
- [Другие взаимодействия](#8-другие-взаимодействия-не-переносимые-вещи)
- [Идентичность и жизненный цикл](#9-идентичность-координаты-и-жизненный-цикл)
- [Границы синхронизации](#10-требования-к-синхронизации-и-проверкам)

## 2. Классификация

| Категория | Представление | Идентичность | Последствия |
|---|---|---|---|
| Бочка/переносимая провизия | `ObjectInteractionHandler`, Rigidbody, Collider | исходное размещение сцены; постоянного id класса нет | покупка, перенос, бросок, пополнение запасов и Destroy |
| Переносимая рыба | тот же класс с `isFauna`, `FishPoint` | точка рыбы и экземпляр | возврат к точке, счётчик рыбного трюма |
| Инструмент инвентаря | `InventoryButton.item`, специальные контроллеры | слот инструмента, не id мировой вещи | видимость, анимация, камера; у молотка ремонт, у оружия убийство |
| Контрактный/собственный товар | `CargoDefinition`, `FixedJoint`, `CargoPlacer` | `id` задаёт тип; `taskID` задаёт задание, не уникальный ящик | размещение, масса, loaded/progress, награда, сохранение |
| Рыба ручной удочки | `Fish` + `FishingController` + диалог | вид `id`, случайные вес/длина | Food и fishRecords, а не свободный предмет после Keep |
| Животное/добыча | `WanderingGameController`, `INPC`, provisionPrefab | сцена животного, затем новый экземпляр добычи | Kill, Harvest, создание Food |
| Стационарное оснащение | двери, снасти, свет, помпа, карты и прочее | индекс/ключ своей подсистемы | не входит автоматически в реестр переносимых вещей |

`IInventoryItem.cs:3` является пустым **классом MonoBehaviour**, несмотря на префикс I.
`IGenericItem` является интерфейсом действия (`PerformAction`, `StopAction`, `GetPrompt`,
`GetGameObject`), но не гарантирует переносимость. Это принципиально разные контракты.

## 3. Переносимые вещи: ObjectInteractionHandler

### Определение и состояние

Источник: `ObjectInteractionHandler.cs:8`, `ObjectInteractionHandler.cs:90`.

| Поле | Доступ | Смысл |
|---|---|---|
| `amount` | private serialized, default 125 | добавляемое количество ресурса |
| `type` | private serialized | `ProvisionType`; меняется через `SetType` |
| `allowPurchase`, `cost` | public serialized, cost default 95 | продажа предмета |
| `purchased` | private | покупка уже совершена |
| `weight` | private, 250 | физическая масса обычного предмета; **не amount** |
| `holdYOffset` | private serialized, -0.2 | мировое вертикальное смещение от viewpoint |
| `isHeld` | public | удержание |
| `parent`, `parentShip`, `viewpoint`, `player` | private | опора после отпускания, корабль ресурсов, локальная точка удержания, плательщик |
| `cs` | public | занятый `CargoSpot` шлюпки |
| `isFauna`, `fishpoint`, `faunaName` | private serialized | особый путь рыбы |
| `namePreset` | public | не выбирать случайное имя в Start |
| `objectName`, `startName` | private | текущий HUD-текст и исходное имя |

`Start` запоминает родителя, Collider/Rigidbody. Обычной вещи включает gravity и задаёт mass=250.
Еда/напитки получают случайную подпись. `amount` определяет запас, а масса тела остаётся 250.
Значения по умолчанию из C# не следует выдавать за значения всех префабов.

Случайные подписи Food: `Dried Figs`, `Dried Biscuits`, `Salted Fish`, `Covetous Beans`,
`Ground Linseed`, `Whole Timberbean`. Drink: `Caelean Wine`, `Watered Rum`, `Fresh Water`,
`Oderous Water`. Oakum/Canvas/Oil имеют фиксированное имя типа с количеством; остальные типы
в этом switch получают пустое имя. Подпись не определяет отдельный ресурс или prefab id.

### Взять, отпустить, бросить

Источник: `InteractionController.cs:965`, `ObjectInteractionHandler.cs:273`,
`ObjectInteractionHandler.cs:330`, `ObjectInteractionHandler.cs:399`.

- Диспетчер Cargo устанавливает player и HUD; при взятии задаёт viewpoint, heldObject,
  `Hold(true)` и, для товара, `Purchase()`.
- `Hold(bool)` меняет флаг. Большинство физических последствий выполняет следующий `Update`.
- При удержании обычной вещи уничтожается FixedJoint, освобождается `cs.occupied`, cs обнуляется;
  тело становится kinematic, parent очищается, вещь становится дочерней viewpoint.
  Положение равно viewpoint.position плюс **мировой** Y-offset, Collider становится trigger.
  Поворот отдельной строкой здесь не задаётся: действует parenting.
- При отпускании обычный Update возвращает parent, снимает kinematic/damping, восстанавливает
  нетриггерный Collider при отсутствии FixedJoint. Raycast вниз может определить корабль и
  менять `WaterObject.enabled`. У непроданной покупной вещи этот путь возвращается раньше.
- `Throw(direction)` сразу снимает удержание/kinematic, возвращает parent и вызывает
  AddForce с `ForceMode.Impulse`. Диспетчер передаёт camera/controller forward × 1500, после
  чего вызывает ещё `Hold(false)`. Это **один бросок**, не бросок плюс второе независимое Drop.
- `SetViewpoint`, `SetPlayer` относятся к действующему игроку. Нельзя воспроизводить чужое
  действие через viewpoint или player хоста.

### Покупка, поглощение, шлюпка

Источник: `ObjectInteractionHandler.cs:174`, `ObjectInteractionHandler.cs:196`,
`ObjectInteractionHandler.cs:431`, `RowboatController.cs:379`.

`Purchase()` однократно ставит purchased, играет coinAudio, списывает cost через
`PlayerHandler.ChangeMoney` и снимает kinematic. Доступность по деньгам проверяет диспетчер,
сам метод не проверяет её заново. `UpdateName()` добавляет Purchase/Can't afford по локальным
деньгам. `SetName()` меняет только objectName, не startName: после UpdateName подпись может
быть восстановлена из startName. Для сетевого имени недостаточно одного SetName.

Вход в LootZone при parentShip != null вызывает `AddProvisions(amount,type)`, показывает prompt
и **уничтожает вещь независимо от успешности пополнения**. Значит отказ при полном складе
также не сохраняет бочку. Вход в FishHold вызывает AddFish/звук и ResetFish.

Столкновение свободной вещи со шлюпкой при cs == null вызывает PlaceCargo: первый свободный
CargoSpot помечается occupied, вещь ставится в его позу, MeshCollider становится trigger,
создаётся FixedJoint к boatRB. `CargoSpot` содержит лишь Transform и bool, это не компонент.
При следующем взятии место освобождается. Это не размещение контрактного CargoDefinition.

### Что действительно находится в дампе

Дамп H: строки 11–125, все 19 записей `captured=Start`, `fauna=False`, `held=False`, tag Cargo.

| Группа | Количество | Типы | Количество ресурса | Слой/тело |
|---|---:|---|---|---|
| tiny_greenland, red_barrell_NEW | 7 | Oakum ×3, Oil ×2, Food ×1, Drink ×1 | 150, 200 или 224 kg по подписи | 7 Interactable; dynamic Rigidbody, MeshCollider |
| homeland/store, red_barrell_NEW | 12 | Food ×4, Drink ×4, Oakum ×4 | 250 kg по подписи | 6 whatIsGround; непроданные kinematic Rigidbody, MeshCollider |

Одна островная Oil-бочка `red_barrell_NEW (2)` уже под `caravel_tutorial` в gameplay у хоста
(дамп H:48), но её сохранённый Start-key указывает на tiny_greenland. В C она осталась на острове.
Это подтверждает необходимость исходного ключа вместо текущего пути. Сопоставление ключей H/C
в предыдущем проходе дало одинаковые 19 кандидатов; это не доказательство для других сцен.

## 4. Ресурсы корабля: ProvisionsController

Источник: `ProvisionsController.cs:7`, `ProvisionsController.cs:111`,
`ProvisionsController.cs:201`, `ProvisionsController.cs:260`.

| Enum (порядок с 0) | Реальный счётчик | Сохранение | Комментарий |
|---|---|---|---|
| Food | currentFood | Food=... | еда, ручной улов, добыча |
| Drink | currentDrink | Drink=... | вода/напитки |
| Oakum | currentOakum | Oakum=... | ремонт течей |
| Timber | currentTimber | Timber=... | есть счётчик и travel-rate; не подтверждён отдельный переносимый prefab |
| Canvas | currentCanvas | Canvas=... | аналогично |
| Oil | currentOil | Oil=... | расход корабельных фонарей |
| Aburae | currentAburae | Aburae=... | присутствует в ресурсах и торговле |
| Powder | нет | нет | только enum в этом контроллере; полноценная поддержка не подтверждена |
| Shot | нет | нет | только enum в этом контроллере; полноценная поддержка не подтверждена |

Итого **семь**, не восемь рабочих current-счётчиков. Add/Remove изменяют currentProvisions
перед switch, у которого нет Powder/Shot. Передача этих enum как обычных запасов нарушит
согласованность суммы: наличие enum не доказывает готовую механику боеприпасов.

Add допускает только `currentProvisions + amount < maxProvisions` (строго меньше).
Remove допускает только `currentProvisions - amount > 0`. Периодическое потребление по
Time.deltaTime: default 30 минут, Food 10, Drink 15, Oil 1 на включённый корабельный фонарь;
по окончании пересчитывается сумма. Это defaults кода, Inspector может переопределять их.
При нехватке масла свет выключается. Save хранит семь запасов и consumption interval.

`BuySellProvisionsDialog.CompleteTransaction` (`:267`) напрямую присваивает семь счётчиков и
прибавляет moneyChangeAmt к GameData.tearCount, минуя Add/Remove. В этой функции нет пересчёта
currentProvisions. `UpdateValues` (`:185`) суммирует без Aburae, хотя SetData включает его.
Следовательно, патчить только AddProvisions недостаточно для общей экономики.

## 5. Личные инструменты

### Каталог слотов из работающей игры

Источник: раздел Inventory buttons дампа H/C. Слой корня не равен слою всех дочерних мешей.

| Индекс buttons | item | Слой корня | enableAsNew | Контроллер/особенность |
|---:|---|---|---|---|
| 0 | quadrant | Default | false | QuadrantController, QuadrantAnimationController, HandSelector |
| 1 | spyglass | UI_Physical | false | SpyglassController, собственная камера/FOV |
| 2 | chronometer_simple | UI_Physical | false | ChronoController |
| 3 | lantern | Default | false | вложенный hand_r/lantern/Light2/lantern; Rigidbody, joint, Collider, particles и Light |
| 4 | handheld_sun_compass | UI_Physical | false | HandheldCompassManager |
| 5 | caulk_hammer | UI_Physical | false | CaulkHammerController |
| 6 | tow_log | UI_Physical | false | ChipLogController |
| 7 | FPSController(Clone) | UI_Physical | **true** | оружейный объект; itemPrefab=FPSController |

`InventoryButton.cs:34`: кнопка выключает другие инструменты, переключает item.activeSelf,
isActive и UI camera, закрывает parentMenu. `Deactivate` (`:64`) для enableAsNew уничтожает
текущий item и создаёт новый из itemPrefab под прежним родителем, затем выключает его.
`InventoryManager.cs:89` FireButton использует родной список buttons. Ссылка на item слота 7
не стабильна даже в одной сессии; нужно повторно получать её после Deactivate.

### Поведение и опасность прямого клонирования

| Инструмент | Подтверждённый код | Что не должно запускаться у визуальной копии |
|---|---|---|
| Квадрант | `QuadrantController.cs:85`: угол камеры, needle, zoomLevel, savedElevation | OnEnable/Disable меняют общий Enviro skybox/material (`:186`); ввод, камеры/UI |
| Труба | `SpyglassController.cs:101`, `:184`: aimed/rest, zoom, camera FOV | ввод, FOV, reflection/player references |
| Хронометр | `ChronoController.cs:68`: стрелки местного времени и prime meridian из Enviro longitude | привязка к локальному player/AudioZone; при отображении брать общее время |
| Солнечный компас | `HandheldCompassManager.cs:72`, `:95`: направление, latitudeAdjustment, overlay/light, savedAzimuth | создание/Enable собственного InputActionController, ввод и глобальные savedAzimuth |
| Молоток | `CaulkHammerController.cs:42`, `:80`: heldObject/isInteracting, анимация hammer_strike, звук, Leak.Repair | OnEnable/Disable меняют глобальную интеракцию; удар не равен только анимации |
| Лаг | `ChipLogController.cs:63`, `:80`: deployed/active, стрелка скорости, rope.length/flywheel | Throw-input, выбор корабля локального игрока, перемещение точки верёвки |
| Оружие | `GunEvents.cs:61`, `:95`, `:179`: cock/ready/loaded/fire/reload, задержанный raycast 250 m, INPC.Kill | Awake берёт Camera.main/player; input, Kill, smoke creation и callbacks |

`GunShoot.cs` использует старый `UnityEngine.Input` и создаёт hit-effects по физическому материалу.
Нельзя по наличию этого класса считать его действующим оружием слота 7: рабочий путь GunEvents
использует новый Input System. В прочитанном GunEvents нет расхода Powder/Shot.

Удалённая модель инструмента должна быть **визуальной**: без player/input/controller scripts,
Camera, интерактивных Collider/physics и UI. Сохранить только нужные меши, кости и управляемую
анимацию/эффекты. First-person руки не должны образовать вторую пару рук у аватара.
Дамп H:1238 подтверждает humanoid Animator и RightHand у CoopAvatar 2. Приборы имеют вложенные
visual roots; весь объект под камерой нельзя просто поставить в кисть без проверки масштаба.
У ручного фонаря дамп H:508 показывает корень под hand_r, два Rigidbody, ConfigurableJoint,
CapsuleCollider, пламя/дым, LODGroup и Light. Копировать его физику вместе с визуальной моделью
нельзя; расход корабельного Oil для этого инструмента текущими источниками не доказан.

## 6. Товарный груз

### Типы из availableCargoTypes

Источник: дамп H:1208. Это все **восемь ссылок данного менеджера в снимке**, не всех ассетов.
У всех type=`cargo`, слой 6 whatIsGround, taskID пустой на prefab/reference.

| Индекс | Объект | CargoDefinition.id | CargoDefinition.name |
|---:|---|---|---|
| 0 | crate 4 | barrells_1 | Fresh Water |
| 1 | crate 5 | crate12 | Dried Fish |
| 2 | crate 6 | crate2 | Linseed |
| 3 | crate 7 | crate1 | Wooden Goods |
| 4 | crate 8 | crate8 | Linen |
| 5 | crate 9 | crate9 | Iron Goods |
| 6 | crate 10 | crate10 | Linen |
| 7 | crate 11 | crate11 | Bronze Ore |

Два Linen имеют **разные id**, нельзя объединять их по имени. Product-prefabs ссылаются на
crate1/crate8/crate9 (WOODPRODUCT/LINENPRODUCT/IRONPRODUCT). Активный корабль caravel имеет
два cargoBoxes, индексы 0 и 1 (дамп H:1232); записи загруженных CargoDefinition в этом разделе
не показаны. Fresh Water как товарный ящик не равен автоматически Drink-бочке.

### Размещение и побочные эффекты

Источники: `CargoDefinition.cs:6`, `CargoPanelHandler.cs:517`, `:555`, `:626`,
`CargoPlacer.cs:96`.

CargoDefinition хранит id/name/type/mass/value/taskID, meshes, clone и текстовые labels.
Через 4 секунды Start-корутина SetupClone активирует clone, находит ShipObjectColliderClone,
ставит clone под parent локального CargoPlacer.cloneTransform и копирует localPosition.
OnDestroy уничтожает clone. `SetName` меняет textLabel, не поле name.
`ShipObjectColliderClone.cs:9` в FixedUpdate повторяет localPosition/localRotation на
статичной навигационной копии; согласовать один видимый ящик без этой копии недостаточно.

- Placement вычисляется локальной камерой и placementSensor; создаёт cargoToLoad, подключает
  FixedJoint к кораблю, меняет parent, задаёт probe/taskID/destination, увеличивает task.loaded,
  вызывает UpdateCargoMass. Базовый путь разрешает загрузку в 400 m от origin.
- Movement меняет позу/parent существующего экземпляра, переподключает joint и probe;
  не создаёт новый ящик и не меняет loaded/progress.
- Removal уничтожает выбранный экземпляр. На контрактной вкладке уменьшает loaded; в пределах
  400 m от destination либо 1000 m от pirateDestination увеличивает progress; при равенстве
  quantity вызывает CompleteTask. На product-вкладке функция возвращается после Destroy,
  до контрактных счётчиков и финального UpdateCargoMass. Это существующий путь игры, не исправление мода.
- CargoPlacer.GetLoadedMass/Count обходят CargoDefinition у **соседей под своим parent**.
  RemoveByTask уничтожает совпадающие taskID и уменьшает quantity/loaded, пересчитывает массу.

`TaskHandler.cs:533` CompleteTask меняет complete/loaded, TaskPanelHandler, logs, debts и деньги;
пиратский множитель зависит от позиции **локального GetPlayer()**, координаты записи берутся
из Enviro. Повторный вызов не является безопасным сетевым применением результата.

### Сохранение

Источник: `ShipHullMover.cs:1927`, `:1945`, `:1971`, `:2045`.

Формат строки loadedCargo: `shipId=cargoTypeId=boxIndex=localPosition=taskID`.
Save обходит cargoBoxes и CargoDefinition под их parent. Уникального id экземпляра в строке нет.
LoadShipCargo читает typeId, но создание выбирает по связанному task-string (поле 6), сравнивая
shipId и taskID; восстанавливает position, rotation родителя, joint, probe и destination.
Требуются уже доступные acceptedTasks/product task strings и availableCargoTypes.
Сетевой манифест экземпляров не следует заменять этими строками: для одинаковых ящиков одного
задания они не дают отдельной идентичности. При первичном связывании можно использовать запись
сейва плюс порядковый экземпляр, но runtime NetId должен выделять хост.

## 7. Рыба и охота

### Ручная рыбалка

Источники: `FishingController.cs:446`, `Fish.cs:38`, `FishDialogManager.cs:54`, `:114`.

Fish случайно выбирает weight, вычисляет length/difficulty и масштаб уже в Awake.
`GetFishIdentity` возвращает `id=weight=length=difficulty=description=rarity`.
Достижение крючком limit ≤ 0.05 показывает диалог. SetDialogData может сразу обновить
GameData.fishRecords, **до решения Keep/Release**. Keep добавляет weight в Food корабля
локального игрока и вызывает Release; Release удаляет UI-рыбу, сбрасывает удочку и ввод/курсор.
Это не путь «создать бочку и положить в трюм». Список видов availableFishPrefabs/availableFish
сериализован и не перечислен текущим I0; названия всех видов здесь намеренно не придуманы.

### Рыба промыслового трюма

Источники: `FishPoint.cs:37`, `ObjectInteractionHandler.cs:282`, `:450`,
`FishCargoManager.cs:39`, `SellFishDialog.cs:117`.

FishPoint управляет видимостью рыбы через fishOn либо наличие чужого parent, масштабом и
подводным флагом; Setup рандомизирует крючок/анимацию. Fauna-путь ObjectInteractionHandler при
удержании снимает fishOn, после отпускания/броска запускает возврат ResetFish через 5 s;
это возврат к исходной точке, а не обычное уничтожение. Дополнительно есть watchdog после
6 s без parent. При FishHold увеличивается счётчик рыбного трюма.

FishCargoManager: currentFishCount, блоки fishBlocks, default fishSetCount=10 и unitWeight=50.
GetTotalMass = count × unitWeight; RecalculateFish сбрасывает отображение и заново вызывает
AddFish; SetCount **только увеличивает**, RemoveFish пустой. Для уменьшения применять SetCount
нельзя. RemoveAllFish сбрасывает все блоки. AddFish допускает рост count и после maxFishCount.
Save корабля хранит fishCargoCount, не отдельные рыбы. SellFishDialog пересчитывает count из
оставшихся тонн, вызывает RecalculateFish и напрямую меняет GameData.tearCount.

CargoFish.PerformAction (`CargoFish.cs:26`) лишь переключает private isHeld; в самом классе
нет переноса, физики или сдачи. Не подменять им полноценный fauna-путь.

### Охота

Источник: `WanderingGameController.cs:105`, `:116`, `GunEvents.cs:179`.

Животное ходит по NavMesh со случайными целями; Kill включает ragdoll Rigidbody, отключает
Animator и останавливает agent. Harvest только у dead создаёт provisionPrefab выше тела,
ставит allowPurchase=false, type Food, namePreset=true, подпись `Wild Game (85..199 kg)` и
уничтожает heiarchicalParent. **amount при этом не меняется**: случайное число в имени не
доказывает количество ресурса. Spawn должен получить отдельную идентичность, а смерть,
удаление тела и создание добычи должны согласовываться как последствия одного действия.

## 8. Другие взаимодействия, не переносимые вещи

| Представление | Реальный результат | Граница |
|---|---|---|
| RopeInteractionHandler, SquareSailBraceController | работа снастей | управление кораблём; не обычный Drop/Throw |
| LightController | SetLightOn/DisableLight/ToggleLight, масло корабля | корабельные фонари уже относятся к ControlsSync; handheld lantern отдельный объект |
| LeakController + молоток | уменьшение intensity/flow, Oakum, отключение течи | `LeakController.cs:74`; визуальный удар не заменяет состояние течи и запасов |
| KinematicUnlocker | снимает Rigidbody.isKinematic, очищает prompt | IGenericItem, но не перенос в руку |
| WorldEpitaph | создаёт NewCrewController, помещает в crew либо crewOwned=id=NONE, удаляет источник | `WorldEpitaph.cs:30`; не инвентарный предмет |
| ShipNameCreator | настройка корабля | IGenericItem не означает физический груз |
| GenericUIItem/TutorialObject/WreckHandler | Read/Inspect/Draft | локальные окна и возможные данные прогресса, не переносимый меш |
| MapController/TaskMenuController/ProvisionsController | взаимодействие с картой/заданиями/запасами | отдельное состояние данных и локальная камера |
| SceneTransition | StartTransition | смена мира/сцены, а не действие с вещью |
| PlankController | создаёт/удаляет plank, переключает HingeJoint.connectedBody | `PlankController.cs:24`; отдельный динамический объект оснащения |
| Cannon | FireCannon: анимация, particles, sound, 25 s cooldown | `Cannon.cs:24`; в этом классе нет расхода Powder/Shot или расчёта попадания |
| RowboatController | шлюпка, гребля, CargoSpot | самостоятельная физика транспорта, пока не синхронизирована |

Перечень смежных систем нужен, чтобы не спутать «всё, что можно нажать» с общей моделью вещи.
Подробности существующих органов управления: [GAME_INTERACTIONS.md](GAME_INTERACTIONS.md).

## 9. Идентичность, координаты и жизненный цикл

Источник мода: `Runtime/ItemCatalog.cs:43`, `:52`, `:82`; источник игры: `Island.cs:74`, `:253`.

1. Кандидат размещения I0: scene.path + путь Transform с ordinal среди одноимённых siblings
   на каждом уровне; имена экранируются. Фиксируется после ObjectInteractionHandler.Start,
   не пересчитывается при переносе. InstanceID, подпись, текущая позиция и текущий parent
   не являются сетевой идентичностью.
2. Fallback для не наблюдавшегося Start явно помечен; поздний dump после переноса не восстанавливает
   исходный путь. Неактивные объекты без Start также не дают автоматически проверенный ключ.
3. Для созданных во время игры вещей scene-path с `(Clone)` не гарантирует уникальность.
   Создание/удаление требуют host NetId, определения типа и устойчивого события создания.
4. На корабле хранить hull-local позу, в мире real-space с OriginShift.LocalOffset; сопоставление
   world position между дампами без учёта offset неверно.
5. Дамп H:129 измеряет viewpoint относительно ступней `(0,1.4019,1.2009)` для standing walking.
   Это мировой вектор одной позы, не универсальная высота и не постоянный forward-offset для
   всех поворотов/приседа. Для удерживаемой вещи нужен полный якорь из позы игрока плюс Y-offset.
6. Island грузит/выгружает detailScene по Camera.main, default maxDetailDistance=1000, проверка
   через интервалы fixedTime (после первого около 40 s), использует среднее дистанций.
   У хоста и клиента набор загруженных предметов может различаться. Отсутствие объекта у хоста
   не доказывает, что объект уничтожен игроком.
7. Перенесённая бочка в дампе H находится уже в gameplay; сохранение её после выгрузки острова
   **ещё не проверено**. Нужны unload/reload и возврат к острову, а не вывод только по parenting.
8. Обычные свободные бочки не имеют IDataPersistence в рассмотренном классе; SaveShipCargo
   сохраняет CargoDefinition, не ObjectInteractionHandler. Долговременное сохранение свободных
   вещей не следует объявлять существующим по сохранению GameData.

## 10. Требования к синхронизации и проверкам

Это **проектные требования**, выведенные из анализа, не реализованные функции Protocol 4.
Актуальная очередность плана: I0 каталог → I1 реестр/манифест → I2 перенос → I3 инструменты →
I4 хозяйство → I5 груз → I6 жизненный цикл. Каждый этап принимается в игре до следующего.

- Хост выделяет NetId и хранит авторитетное состояние общего мира; клиент выполняет действие
  локально и сообщает результат. Не добавлять проверки прав/дистанции/чисел от мода:
  полный trust-контракт сохраняется. Штатные условия игры описаны выше только как факты.
- В руке вещь выводится из позы владельца, без отдельного потока held pose. Свободную физику
  считает хост, гости интерполируют; Update игры не должен перезаписывать remote parent/body.
- Take/Drop/Throw и создание/потребление/удаление различать; убрать эхо и двойное применение
  Throw + Hold(false), списания денег, ресурсов, loaded/progress и наград.
- Инструмент: выбор слота + нужное визуальное состояние; не запускать удалённые игровые скрипты.
  Пересоздание оружия, отключение игрока и смена мира требуют снятия визуальной копии.
- Общая экономика: деньги, семь запасов, interval, задания/долги/логи; перехватить прямые
  записи диалогов наряду с методами ресурсов. Ремонт передаёт и течь, и Oakum.
- Cargo: отдельный экземпляр, type id, taskID, box/parent/pose, joint/clone readiness и масса.
  Сдача передаёт результат машины игрока; CompleteTask на player хоста не повторять.
- Удаление хранить как факт потребления/сдачи/Harvest, отдельно от локальной выгрузки сцены.
  Поздний вход должен получить уже перенесённые/удалённые вещи и не возродить их из острова.

### Обнаруженные особенности игры, которые нельзя скрыть синхронизацией

Подтверждены чтением кода, **не новым runtime-тестом**; код игры/мода здесь не исправлен:

- LootZone удаляет ресурс даже при отказе AddProvisions из-за вместимости.
- Wild Game показывает случайный вес, но Harvest не устанавливает amount.
- Powder/Shot существуют в enum без соответствующих ресурсных счётчиков/сейва.
- BuySellProvisionsDialog имеет прямые присваивания, непересчитанную сумму и расчёт без Aburae.
- FishCargoManager.SetCount не уменьшает количество; AddFish допускает превышение максимума.
- Повторное CompleteTask способно повторить экономические последствия; награда зависит от игрока.

### Матрица дальнейшей игровой приёмки

| Сценарий | Что наблюдать |
|---|---|
| Взять/отпустить/бросить бочку обоими игроками | один владелец, совпадение free pose, отсутствие двойного Drop |
| Стоя/в приседе, взгляд вверх/вниз, палуба/берег | полный viewpoint offset и ориентация, не только высота |
| Покупка и LootZone, склад полный | однократные деньги/ресурс/Destroy; штатный отказ не маскировать |
| Перенос на корабль и отход от острова | unload/reload без исчезновения вещи и без дубля на исходном месте |
| Остров загружен только у гостя | создание/ключ/отсутствие у хоста не трактуются как удаление |
| Все восемь инструментов, быстрый switch, slot 7 reset | верная модель, нет лишних рук/камер/input/побочных эффектов |
| Молоток и Oakum | совпадает intensity течи и запас, удар не применяется дважды |
| Два ящика одного taskID, два Linen id | уникальные экземпляры, правильные типы |
| Cargo place/move/remove/save/load | parent/joint/clone, масса и task.loaded/progress согласованы |
| Ручной улов и промысловая рыба | различаются Food/fishRecords и fishCargoCount; Keep/Release/продажа |
| Kill/Harvest | одна смерть, одна добыча, корректный amount независимо от подписи |
| Поздний вход/выход держателя/перезагрузка мира | нет потерянных предметов, устаревших visual refs и возрождённых удалений |

Документ проверен по указанным исходникам и доступным снимкам. Новая игровая приёмка,
извлечение полного asset-каталога и реализация I1–I6 в эту работу не входят.
