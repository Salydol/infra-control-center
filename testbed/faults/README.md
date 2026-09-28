# Инжектор сбоев

Внедряет сбои шести классов в демо-магазин, откатывает их и записывает
разметку каждого инцидента в JSONL. Только стандартная библиотека Python 3.11+.

```bash
cd testbed/faults
python -m faults list                                  # сценарии и приманки
python -m faults status                                # здоровье стенда
python -m faults inject M2 --hold 60 --quiet 15        # один инцидент
python -m faults inject N1 --decoy X5b                 # с приманкой
python -m faults campaign night-01 --per-class 20      # 120 инцидентов, возобновляемо
python -m faults verify ../runs/night-01/incidents.jsonl
python -m faults restore                               # откатить незавершённый инцидент
python -m faults restore --pristine                    # вернуть стенд к эталону
```

Топология — `topology.local.toml` (всё на этой машине) или своя копия
`topology.vms.example.toml` для трёх ВМ (`--topology topology.vms.toml`,
команды идут по SSH).

## Классы и сценарии

| Класс | Связан с изменением | Сценарии |
|---|---|---|
| `misconfig` — неверная настройка | да | M1–M7: `appsettings.json` (на лету или с рестартом), env-файлы |
| `bad_deploy` — неудачный деплой | да | D1–D5: новая версия образа со вшитым дефектом |
| `manual_edit` — ручная правка по SSH | да | H1–H4: `compose.yaml`, `nginx.conf` в обход обычного процесса |
| `resource` — ресурсы | нет | R1–R4: stress-ng в cgroup контейнера (Pumba), «шумный сосед» на узле |
| `network` — сеть | нет | N1–N4: задержка и потери пакетов (Pumba netem) |
| `dependency` — падение зависимости | нет | P1–P4: `docker kill`, `docker pause` |

**Приманки** X1–X8 — безвредные изменения (правка настроек, обычный релиз,
новый флаг в env, правка nginx или комментария в compose). В кампании они
по умолчанию делаются перед каждым сбоем, не связанным с изменением, за 1–4
минуты до него, и никогда не трогают те же файлы, что и сбой. В половине
случаев приманка попадает на тот же сервис, что и сбой, — это самый трудный
случай для правила «виновато последнее изменение».

**Неудачный деплой** снаружи неотличим от обычного релиза: версия растёт
общим счётчиком (`1.0.N`), а дефект вшит в метаданные сборки
(`-p:BuildFault=…`), а не в окружение. Иначе он был бы виден агенту в diff
окружения, и разметка протекла бы в признаки модели.

## Цикл инцидента

```
здоровье стенда → [приманка → 60–240 с] → сбой → удержание (8 мин)
  → откат → ожидание здоровья → тишина (5 мин) → запись разметки
```

Около 20 минут на инцидент, 120 инцидентов — около 40 часов.

Перед применением каждого действия запись для отката сохраняется в
`runs/current.json`, поэтому `restore` откатывает инцидент даже после падения
инжектора. Эталонные копии всех изменяемых файлов лежат в `runs/pristine/`.

## Разметка

Одна строка JSON на инцидент (`runs/<кампания>/incidents.jsonl`):

```json
{
  "id": "20261012-031502-M2",
  "scenario": "M2", "class": "misconfig", "change_related": true,
  "root_cause": {
    "category": "config", "service": "payments-worker", "node": "node2",
    "container": "shop-payments-worker-1",
    "file": "config/payments-worker/appsettings.json", "applied_by": "hot-reload",
    "changes": [{"key": "Payments:ProviderTimeoutMs", "before": "1000", "after": "40"}]
  },
  "decoy": null,
  "timeline": {"fault_started_at": "…", "injected_at": "…", "recovery_started_at": "…",
               "recovered_at": "…", "healthy_at": "…"},
  "outcome": {"recovered_healthy": true, "errors": []}
}
```

Вспомогательные контейнеры инжектора (Pumba, stress-ng) помечены
`icc.fault-injector=true`: агент ICC их игнорирует, иначе модель научилась бы
находить причину по появлению контейнера Pumba.
