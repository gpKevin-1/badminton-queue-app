<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { VueDraggableNext as draggable } from 'vue-draggable-next';

// --- 1. 資料型別定義 ---
export interface Player {
  id: number | string;
  name: string;
  gender: string; // 'M' | 'F' | '-' (無/未指定)
  playedCount: number;
  level: string; // 1 ~ 15 級
  status?: '1' | '0'; // '1': 排隊中/比賽中, '0': 休息/請假
  sessions: string[]; // 選手報名的時段標籤
  partnerId?: number | string | null; // 👥 綁定搭檔 ID
  queueEnteredAt?: number | null;     // ⏳ 進入等待佇列的時間戳記（計算等待時間）
  lastMatchPeers?: string[];          // 🎾 上局同場的球員姓名列表
  avoidIds?: (number | string)[];     // ⛔ 需要錯開（不希望同場）的球員 ID 列表
}

export interface MatchHistoryRecord {
  id: string;
  timestamp: number;
  courtName: string;
  playerNames: string[];
}

export interface Court {
  id: number;
  name: string;
  players: Player[];        // 目前正在場上的球員
  nextPlayers: Player[];    // 預先排點區的球員
  status: 'idle' | 'playing';
  startTime?: number | null;
  elapsedSeconds?: number;
}

export interface SessionConfig {
  courtCount: number;
  maxPlayersCount: number;
  maxGamesPerPlayer: number;
  timeSlots: string[];      // 今日開啟的時段列表
}

// --- 時段解析與排序輔助函式 ---
const parseTimeSlot = (slotStr: string): number => {
  const match = slotStr.match(/\d+(:?\d+)?/);
  if (!match) return 0;
  const timeStr = match[0];
  if (timeStr.includes(':')) {
    const [h, m] = timeStr.split(':').map(Number);
    return h * 100 + (m || 0);
  }
  return Number(timeStr) * 100;
};

// --- 2. 狀態宣告 ---
const STORAGE_KEY_PLAYERS = 'badminton_queue_players_data';
const STORAGE_KEY_CONFIG = 'badminton_queue_config_data';
const STORAGE_KEY_HISTORY = 'badminton_queue_history_data';

const DEFAULT_TIME_SLOTS = ['18:00-20:00', '20:00-22:00'];

const currentView = ref<'setup' | 'board'>('setup');

const sessionConfig = ref<SessionConfig>({
  courtCount: 3,
  maxPlayersCount: 30,
  maxGamesPerPlayer: 4,
  timeSlots: [...DEFAULT_TIME_SLOTS]
});

const newSlotInput = ref<string>('');
const currentSessionFilter = ref<string>('all');

const sortedTimeSlots = computed(() => {
  return [...sessionConfig.value.timeSlots].sort((a, b) => parseTimeSlot(a) - parseTimeSlot(b));
});

const queuePlayers = ref<Player[]>([]);
const matchHistories = ref<MatchHistoryRecord[]>([]); // 📊 全局對戰歷史紀錄
const isLoading = ref<boolean>(false);
const isDev = ref<boolean>(false);

// 用於即時計算等待時間的當前時間戳
const currentTime = ref<number>(Date.now());

// 批次報名相關狀態
const batchText = ref<string>('');
const batchDefaultGender = ref<string>('-');
const batchDefaultLevel = ref<string>('5');
const batchDefaultSessions = ref<string[]>([...DEFAULT_TIME_SLOTS]);

// Modal 狀態
const isEditModalOpen = ref<boolean>(false);
const editingPlayer = ref<Player | null>(null);

const isAddModalOpen = ref<boolean>(false);
const newPlayer = ref<Omit<Player, 'id'>>({
  name: '',
  gender: '-',
  playedCount: 0,
  level: '5',
  status: '1',
  sessions: [],
  partnerId: null,
  avoidIds: []
});

// 📊 對戰紀錄 Modal 狀態
const isHistoryModalOpen = ref<boolean>(false);

const isBatchMode = ref<boolean>(false);
const selectedPlayerIds = ref<(number | string)[]>([]);

const collapsedGroups = ref<Record<string, boolean>>({
  high: false, mid: false, low: false,
  fresh: false, moderate: false, veteran: false,
  restingGroup: false, completedGroup: false
});

const courts = ref<Court[]>([]);

// --- 動態上限與限制邏輯 ---
const getPlayerMaxGames = (player: Player): number => {
  const baseMax = sessionConfig.value.maxGamesPerPlayer || 4;
  const sessionCount = player.sessions && player.sessions.length > 0 ? player.sessions.length : 1;
  return baseMax * sessionCount;
};

const isPlayerLimitReached = (player: Player): boolean => {
  return player.playedCount >= getPlayerMaxGames(player);
};

const getPartner = (player: Player): Player | undefined => {
  if (!player.partnerId) return undefined;
  return queuePlayers.value.find(p => String(p.id) === String(player.partnerId));
};

// --- 等待時間格式化 ---
const getWaitSeconds = (player: Player): number => {
  if (!player.queueEnteredAt) return 0;
  const diff = Math.floor((currentTime.value - player.queueEnteredAt) / 1000);
  return diff > 0 ? diff : 0;
};

const formatWaitTime = (seconds: number): string => {
  const mins = Math.floor(seconds / 60);
  const secs = seconds % 60;
  if (mins >= 60) {
    const hrs = Math.floor(mins / 60);
    const remainMins = mins % 60;
    return `${hrs}h${remainMins}m`;
  }
  return `${mins}分${String(secs).padStart(2, '0')}秒`;
};

// --- 計算某兩位球員過去對戰/同場過幾次 ---
const getPairMatchCount = (nameA: string, nameB: string): number => {
  let count = 0;
  matchHistories.value.forEach(record => {
    if (record.playerNames.includes(nameA) && record.playerNames.includes(nameB)) {
      count++;
    }
  });
  return count;
};

// --- 時段管理邏輯 ---
const addTimeSlot = () => {
  const val = newSlotInput.value.trim();
  if (!val) return;
  if (sessionConfig.value.timeSlots.includes(val)) {
    alert('該時段已存在！');
    return;
  }
  sessionConfig.value.timeSlots.push(val);
  sessionConfig.value.timeSlots.sort((a, b) => parseTimeSlot(a) - parseTimeSlot(b));
  
  if (!batchDefaultSessions.value.includes(val)) {
    batchDefaultSessions.value.push(val);
  }
  newSlotInput.value = '';
};

const removeTimeSlot = (index: number) => {
  const removed = sessionConfig.value.timeSlots.splice(index, 1)[0];
  batchDefaultSessions.value = batchDefaultSessions.value.filter(s => s !== removed);
};

const resetTimeSlotsToDefault = () => {
  sessionConfig.value.timeSlots = [...DEFAULT_TIME_SLOTS];
  batchDefaultSessions.value = [...DEFAULT_TIME_SLOTS];
};

const initCourts = (count: number) => {
  const newCourts: Court[] = [];
  for (let i = 1; i <= count; i++) {
    const existing = courts.value.find(c => c.id === i);
    if (existing) {
      newCourts.push(existing);
    } else {
      newCourts.push({
        id: i,
        name: `${i}號場`,
        players: [],
        nextPlayers: [],
        status: 'idle',
        startTime: null,
        elapsedSeconds: 0
      });
    }
  }
  courts.value = newCourts;
};

// --- localStorage 操作 ---
const saveToLocalStorage = () => {
  localStorage.setItem(STORAGE_KEY_PLAYERS, JSON.stringify(queuePlayers.value));
  localStorage.setItem(STORAGE_KEY_CONFIG, JSON.stringify(sessionConfig.value));
  localStorage.setItem(STORAGE_KEY_HISTORY, JSON.stringify(matchHistories.value));
};

const loadFromLocalStorage = () => {
  const configData = localStorage.getItem(STORAGE_KEY_CONFIG);
  if (configData) {
    try {
      const parsed = JSON.parse(configData);
      const loadedSlots = parsed.timeSlots || [...DEFAULT_TIME_SLOTS];
      loadedSlots.sort((a: string, b: string) => parseTimeSlot(a) - parseTimeSlot(b));

      sessionConfig.value = {
        ...parsed,
        timeSlots: loadedSlots
      };
      batchDefaultSessions.value = [...sessionConfig.value.timeSlots];
    } catch (e) {
      console.error('解析 Config 失敗:', e);
    }
  }

  const historyData = localStorage.getItem(STORAGE_KEY_HISTORY);
  if (historyData) {
    try {
      matchHistories.value = JSON.parse(historyData);
    } catch (e) {
      console.error('解析 History 失敗:', e);
    }
  }

  const playerData = localStorage.getItem(STORAGE_KEY_PLAYERS);
  const now = Date.now();
  if (playerData) {
    try {
      const parsedPlayers = JSON.parse(playerData);
      queuePlayers.value = parsedPlayers.map((p: any) => ({
        ...p,
        sessions: Array.isArray(p.sessions) ? p.sessions : (sessionConfig.value.timeSlots.slice(0, 1)),
        partnerId: p.partnerId || null,
        queueEnteredAt: p.queueEnteredAt || now,
        lastMatchPeers: p.lastMatchPeers || [],
        avoidIds: p.avoidIds || []
      }));
    } catch (e) {
      console.error('解析 Players 失敗:', e);
    }
  } else {
    const defaultSlots = sessionConfig.value.timeSlots;
    queuePlayers.value = [
      { id: 1, name: '戴資穎', gender: 'F', playedCount: 0, level: '15', status: '1', sessions: [...defaultSlots], partnerId: null, queueEnteredAt: now, lastMatchPeers: [], avoidIds: [] },
      { id: 2, name: '林丹', gender: 'M', playedCount: 0, level: '15', status: '1', sessions: [defaultSlots[0] || '18:00-20:00'], partnerId: null, queueEnteredAt: now, lastMatchPeers: [], avoidIds: [] },
      { id: 3, name: '周天成', gender: 'M', playedCount: 0, level: '11', status: '1', sessions: [defaultSlots[1] || '20:00-22:00'], partnerId: null, queueEnteredAt: now, lastMatchPeers: [], avoidIds: [] },
      { id: 4, name: '山口茜', gender: 'F', playedCount: 0, level: '14', status: '1', sessions: [...defaultSlots], partnerId: null, queueEnteredAt: now, lastMatchPeers: [], avoidIds: [] }
    ];
  }
  initCourts(sessionConfig.value.courtCount);
};

const addOrMergePlayer = (playerData: Omit<Player, 'id'>, nowTimestamp: number, offsetIndex: number): boolean => {
  const cleanName = playerData.name.trim();
  if (!cleanName) return false;

  const existingPlayer = queuePlayers.value.find(p => p.name.trim() === cleanName);

  if (existingPlayer) {
    const combinedSessions = Array.from(new Set([...existingPlayer.sessions, ...playerData.sessions]));
    existingPlayer.sessions = combinedSessions;
    if (playerData.gender !== '-' && existingPlayer.gender === '-') {
      existingPlayer.gender = playerData.gender;
    }
    return false;
  } else {
    queuePlayers.value.push({
      id: nowTimestamp + offsetIndex,
      ...playerData,
      name: cleanName,
      queueEnteredAt: nowTimestamp,
      lastMatchPeers: [],
      avoidIds: []
    });
    return true;
  }
};

const parseAndImportText = () => {
  if (!batchText.value.trim()) return alert('請先貼上選手名單！');

  const lines = batchText.value.split('\n');
  let now = Date.now();
  let countAdded = 0;
  let countMerged = 0;

  lines.forEach((line, index) => {
    let cleanLine = line.trim();
    if (!cleanLine) return;

    cleanLine = cleanLine.replace(/^[\d]+[\.\)\-\s]+/, '').trim();
    if (!cleanLine) return;

    let gender = batchDefaultGender.value;
    let level = batchDefaultLevel.value;

    if (cleanLine.includes('🚺') || cleanLine.includes('♀')) {
      gender = 'F';
      cleanLine = cleanLine.replace(/🚺/g, '').replace(/♀/g, '').trim();
    } else if (cleanLine.includes('🚹') || cleanLine.includes('♂')) {
      gender = 'M';
      cleanLine = cleanLine.replace(/🚹/g, '').replace(/♂/g, '').trim();
    }

    const tokens = cleanLine.split(/[\s,，]+/).filter(Boolean);
    if (tokens.length === 0) return;

    const name = tokens[0];
    if (!name) return;

    let detectedSessions: string[] = [];

    tokens.slice(1).forEach(token => {
      const upper = token.toUpperCase();
      if (['M', '男', 'MALE'].includes(upper)) gender = 'M';
      else if (['F', '女', 'FEMALE'].includes(upper)) gender = 'F';

      const numMatch = token.match(/\d+/);
      if (numMatch) {
        const lvlNum = parseInt(numMatch[0], 10);
        if (lvlNum >= 1 && lvlNum <= 15) {
          level = String(lvlNum);
        }
      }

      sessionConfig.value.timeSlots.forEach(slot => {
        if (token.includes(slot) || slot.includes(token)) {
          if (!detectedSessions.includes(slot)) detectedSessions.push(slot);
        }
      });
    });

    const finalSessions = detectedSessions.length > 0
      ? detectedSessions
      : (batchDefaultSessions.value.length > 0 ? [...batchDefaultSessions.value] : [...sessionConfig.value.timeSlots]);

    const isNewItem = addOrMergePlayer({
      name,
      gender,
      level,
      playedCount: 0,
      status: '1',
      sessions: finalSessions,
      partnerId: null,
      avoidIds: []
    }, now, index);

    if (isNewItem) countAdded++;
    else countMerged++;
  });

  saveToLocalStorage();
  alert(`解析完成！新增 ${countAdded} 位全新選手，合併 ${countMerged} 位重複選手時段。`);
  batchText.value = '';
};

const handleFileUpload = (event: Event) => {
  const file = (event.target as HTMLInputElement).files?.[0];
  if (!file) return;

  const reader = new FileReader();
  reader.onload = (e) => {
    const text = e.target?.result as string;
    if (!text) return;

    const lines = text.split(/\r\n|\n/);
    let now = Date.now();
    let countAdded = 0;
    let countMerged = 0;

    lines.forEach((line, index) => {
      if (!line.trim()) return;
      const cols = line.split(',').map(c => c.trim().replace(/^"|"$/g, ''));
      
      if (index === 0 && (cols[0].includes('姓名') || cols[0].includes('Name'))) return;

      let name = cols[0];
      if (!name) return;

      let gender = batchDefaultGender.value;
      if (name.includes('🚺')) { gender = 'F'; name = name.replace(/🚺/g, '').trim(); }
      if (name.includes('🚹')) { gender = 'M'; name = name.replace(/🚹/g, '').trim(); }

      if (cols[1]) {
        const gUpper = cols[1].toUpperCase();
        if (['F', '女', '🚺'].includes(gUpper)) gender = 'F';
        else if (['M', '男', '🚹'].includes(gUpper)) gender = 'M';
      }

      let level = cols[2] ? String(parseInt(cols[2], 10) || batchDefaultLevel.value) : batchDefaultLevel.value;

      let sessions: string[] = [];
      if (cols[3]) {
        const rawSess = cols[3].split(/[/;|]/);
        sessionConfig.value.timeSlots.forEach(slot => {
          if (rawSess.some(s => s.includes(slot) || slot.includes(s))) {
            sessions.push(slot);
          }
        });
      }
      if (sessions.length === 0) {
        sessions = batchDefaultSessions.value.length > 0 ? [...batchDefaultSessions.value] : [...sessionConfig.value.timeSlots];
      }

      const isNewItem = addOrMergePlayer({
        name,
        gender,
        level,
        playedCount: 0,
        status: '1',
        sessions,
        partnerId: null,
        avoidIds: []
      }, now, index);

      if (isNewItem) countAdded++;
      else countMerged++;
    });

    saveToLocalStorage();
    alert(`CSV 匯入完成！新增 ${countAdded} 位全新選手，合併 ${countMerged} 位重複選手時段。`);
  };
  reader.readAsText(file);
};

const startSession = () => {
  if (sessionConfig.value.courtCount < 1) return alert('請至少設定 1 個場地！');
  if (sessionConfig.value.maxPlayersCount < 1) return alert('請設定有效的總人數上限！');
  if (sessionConfig.value.maxGamesPerPlayer < 1) return alert('每人最多打場數必須至少為 1 場！');
  if (sessionConfig.value.timeSlots.length === 0) return alert('請至少設定一個今日開場時段！');

  initCourts(sessionConfig.value.courtCount);
  saveToLocalStorage();
  currentView.value = 'board';
};

const reopenSetup = () => { currentView.value = 'setup'; };

// --- 5. 佇列排序與分區計算 ---
const queueGroupMode = ref<'level' | 'playedCount' | 'resting'>('level');

const toggleGroupMode = (mode: 'level' | 'playedCount' | 'resting') => {
  queueGroupMode.value = mode;
  selectedPlayerIds.value = [];
};

const filteredQueuePlayers = computed(() => {
  if (currentSessionFilter.value === 'all') return queuePlayers.value;
  return queuePlayers.value.filter(p => p.sessions && p.sessions.includes(currentSessionFilter.value));
});

const currentTabPlayers = computed(() => {
  if (queueGroupMode.value === 'resting') {
    return filteredQueuePlayers.value.filter(p => String(p.status) === '0');
  } else {
    return filteredQueuePlayers.value.filter(p => String(p.status) !== '0');
  }
});

const sortedQueuePlayers = computed(() => {
  return [...filteredQueuePlayers.value].sort((a, b) => {
    if (queueGroupMode.value === 'level') {
      const levelA = Number(a.level) || 0;
      const levelB = Number(b.level) || 0;
      if (levelB !== levelA) return levelB - levelA;
      return a.playedCount - b.playedCount;
    } else {
      if (a.playedCount !== b.playedCount) return a.playedCount - b.playedCount;
      return (Number(b.level) || 0) - (Number(a.level) || 0);
    }
  });
});

const groupedQueuePlayers = computed(() => {
  if (queueGroupMode.value === 'resting') {
    const restingPlayers = sortedQueuePlayers.value.filter(p => String(p.status) === '0');
    return [{ title: '💤 休息請假中', key: 'restingGroup', players: restingPlayers }];
  }

  const activePlayers = sortedQueuePlayers.value.filter(p => String(p.status) !== '0');
  const availablePlayers = activePlayers.filter(p => !isPlayerLimitReached(p));
  const completedPlayers = activePlayers.filter(p => isPlayerLimitReached(p));

  const resultList = [];

  if (queueGroupMode.value === 'level') {
    const high: Player[] = [];
    const mid: Player[] = [];
    const low: Player[] = [];

    availablePlayers.forEach(p => {
      const lvl = Number(p.level) || 0;
      if (lvl >= 11) high.push(p);
      else if (lvl >= 6) mid.push(p);
      else low.push(p);
    });

    resultList.push(
      { title: '🔥 高階分區 (11~15級)', key: 'high', players: high },
      { title: '⚡ 中階分區 (6~10級)', key: 'mid', players: mid },
      { title: '🌱 初階分區 (1~5級)', key: 'low', players: low }
    );
  } else {
    const fresh: Player[] = [];
    const moderate: Player[] = [];
    const veteran: Player[] = [];

    availablePlayers.forEach(p => {
      const count = p.playedCount || 0;
      if (count === 0) fresh.push(p);
      else if (count <= 2) moderate.push(p);
      else veteran.push(p);
    });

    resultList.push(
      { title: '🆕 未上場 (0場)', key: 'fresh', players: fresh },
      { title: '🎾 已上場一半 (1~2場)', key: 'moderate', players: moderate },
      { title: '🔥 好多場 (3場以上)', key: 'veteran', players: veteran }
    );
  }

  if (completedPlayers.length > 0) {
    resultList.push({
      title: `🏁 已達個人打球上限 (已完賽)`,
      key: 'completedGroup',
      players: completedPlayers
    });
  }

  return resultList;
});

const toggleGroupCollapse = (key: string) => {
  collapsedGroups.value[key] = !collapsedGroups.value[key];
};

// --- 6. 計時器邏輯 ---
const timerInterval = ref<ReturnType<typeof setInterval> | null>(null);

const startGlobalTimer = () => {
  if (timerInterval.value) return;
  timerInterval.value = setInterval(() => {
    const now = Date.now();
    currentTime.value = now; 
    courts.value.forEach(court => {
      if (court.status === 'playing' && court.startTime) {
        court.elapsedSeconds = Math.floor((now - court.startTime) / 1000);
      }
    });
  }, 1000);
};

const formatTime = (seconds?: number): string => {
  if (!seconds || seconds < 0) return '00:00';
  const mins = Math.floor(seconds / 60);
  const secs = seconds % 60;
  return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
};

onMounted(() => {
  loadFromLocalStorage();
  startGlobalTimer();
});

onUnmounted(() => {
  if (timerInterval.value) clearInterval(timerInterval.value);
});

// --- 7. 拖曳與看板操作 ---
const getDraggedPlayer = (evt: any): Player | null => {
  return evt?.item?._underlying_vm_ || evt?.draggedContext?.element || null;
};

const handlePlayerAddedToCourt = (evt: any) => {
  const player = getDraggedPlayer(evt);
  if (!player) return;

  if (isPlayerLimitReached(player)) {
    alert(`【${player.name}】已打滿個人上限 (${getPlayerMaxGames(player)} 場)，無法再安排上場！`);
    handlePlayerAddedToQueue(evt);
    return;
  }

  const idx = queuePlayers.value.findIndex(p => String(p.id) === String(player.id));
  if (idx !== -1) {
    queuePlayers.value.splice(idx, 1);
    saveToLocalStorage();
  }
};

const handlePlayerAddedToQueue = (evt: any) => {
  const player = getDraggedPlayer(evt);
  if (!player) return;

  courts.value.forEach(court => {
    court.players = court.players.filter(p => String(p.id) !== String(player.id));
    court.nextPlayers = court.nextPlayers.filter(p => String(p.id) !== String(player.id));
  });

  if (!queuePlayers.value.some(p => String(p.id) === String(player.id))) {
    player.queueEnteredAt = Date.now(); 
    queuePlayers.value.push(player);
    saveToLocalStorage();
  }
};

const openAddModal = () => {
  if (queuePlayers.value.length >= sessionConfig.value.maxPlayersCount) {
    if (!confirm(`目前人數 (${queuePlayers.value.length}人) 已達到上限 (${sessionConfig.value.maxPlayersCount}人)，確定要繼續新增嗎？`)) {
      return;
    }
  }
  newPlayer.value = { 
    name: '', 
    gender: '-', 
    playedCount: 0, 
    level: '5', 
    status: '1',
    sessions: [...sessionConfig.value.timeSlots],
    partnerId: null,
    avoidIds: []
  };
  isAddModalOpen.value = true;
};

const closeAddModal = () => { isAddModalOpen.value = false; };

const saveNewPlayer = () => {
  if (!newPlayer.value.name.trim()) return alert('請輸入選手姓名！');

  addOrMergePlayer({
    name: newPlayer.value.name.trim(),
    gender: newPlayer.value.gender,
    playedCount: Number(newPlayer.value.playedCount || 0),
    level: String(newPlayer.value.level),
    status: newPlayer.value.status || '1',
    sessions: newPlayer.value.sessions && newPlayer.value.sessions.length > 0 
      ? [...newPlayer.value.sessions] 
      : [...sessionConfig.value.timeSlots],
    partnerId: null,
    avoidIds: []
  }, Date.now(), 0);

  saveToLocalStorage();
  closeAddModal();
};

const openEditModal = (player: Player) => {
  editingPlayer.value = JSON.parse(JSON.stringify(player));
  if (!editingPlayer.value!.sessions) {
    editingPlayer.value!.sessions = [...sessionConfig.value.timeSlots];
  }
  if (!editingPlayer.value!.avoidIds) {
    editingPlayer.value!.avoidIds = [];
  }
  isEditModalOpen.value = true;
};

const closeEditModal = () => {
  isEditModalOpen.value = false;
  editingPlayer.value = null;
};

const savePlayerEdit = () => {
  if (!editingPlayer.value) return;
  const targetId = editingPlayer.value.id;
  const newPartnerId = editingPlayer.value.partnerId ? String(editingPlayer.value.partnerId) : null;

  const oldPlayer = queuePlayers.value.find(p => String(p.id) === String(targetId));
  const oldPartnerId = oldPlayer?.partnerId ? String(oldPlayer.partnerId) : null;

  queuePlayers.value = queuePlayers.value.map(p => {
    if (String(p.id) === String(targetId)) {
      return { ...editingPlayer.value!, partnerId: newPartnerId };
    }
    return p;
  });

  if (oldPartnerId !== newPartnerId) {
    if (oldPartnerId) {
      queuePlayers.value.forEach(p => {
        if (String(p.id) === oldPartnerId && String(p.partnerId) === String(targetId)) {
          p.partnerId = null;
        }
      });
    }
    if (newPartnerId) {
      queuePlayers.value.forEach(p => {
        if (String(p.id) === newPartnerId) {
          if (p.partnerId && String(p.partnerId) !== String(targetId)) {
            const prevPartner = queuePlayers.value.find(x => String(x.id) === String(p.partnerId));
            if (prevPartner) prevPartner.partnerId = null;
          }
          p.partnerId = targetId;
        }
      });
    }
  }

  saveToLocalStorage();
  closeEditModal();
};

const toggleBatchMode = () => {
  isBatchMode.value = !isBatchMode.value;
  selectedPlayerIds.value = [];
};

const toggleSelectPlayer = (player: Player) => {
  const isRestingTab = queueGroupMode.value === 'resting';
  const isPlayerResting = String(player.status) === '0';
  if (isRestingTab !== isPlayerResting) return;

  const index = selectedPlayerIds.value.indexOf(player.id);
  if (index > -1) {
    selectedPlayerIds.value.splice(index, 1);
  } else {
    selectedPlayerIds.value.push(player.id);
  }
};

const toggleSelectAll = () => {
  const targetIds = currentTabPlayers.value.map(p => p.id);
  if (selectedPlayerIds.value.length === targetIds.length && targetIds.length > 0) {
    selectedPlayerIds.value = [];
  } else {
    selectedPlayerIds.value = [...targetIds];
  }
};

const updateBatchStatus = (targetStatus: '0' | '1') => {
  if (selectedPlayerIds.value.length === 0) return;
  queuePlayers.value.forEach(p => {
    if (selectedPlayerIds.value.includes(p.id)) {
      p.status = targetStatus;
      if (targetStatus === '1') p.queueEnteredAt = Date.now();
    }
  });
  saveToLocalStorage();
  selectedPlayerIds.value = [];
  isBatchMode.value = false;
};

const batchResetPlayedCount = () => {
  if (selectedPlayerIds.value.length === 0) return;
  if (!confirm(`確定要將選中的 ${selectedPlayerIds.value.length} 位選手已打場數歸零嗎？`)) return;

  queuePlayers.value.forEach(p => {
    if (selectedPlayerIds.value.includes(p.id)) {
      p.playedCount = 0;
      p.lastMatchPeers = []; // 🧹 場次歸零時，同步清除上局同場對象資訊
    }
  });
  saveToLocalStorage();
  selectedPlayerIds.value = [];
  isBatchMode.value = false;
};

const batchDeletePlayers = () => {
  if (selectedPlayerIds.value.length === 0) return;
  if (!confirm(`確定要刪除選中的 ${selectedPlayerIds.value.length} 位選手嗎？`)) return;

  queuePlayers.value.forEach(p => {
    if (p.partnerId && selectedPlayerIds.value.includes(p.partnerId)) {
      p.partnerId = null;
    }
  });

  queuePlayers.value = queuePlayers.value.filter(p => !selectedPlayerIds.value.includes(p.id));
  saveToLocalStorage();
  selectedPlayerIds.value = [];
  isBatchMode.value = false;
};

const removeSinglePlayerInSetup = (id: number | string) => {
  queuePlayers.value.forEach(p => {
    if (String(p.partnerId) === String(id)) p.partnerId = null;
  });
  queuePlayers.value = queuePlayers.value.filter(p => String(p.id) !== String(id));
  saveToLocalStorage();
};

const clearAllPlayersInSetup = () => {
  if (confirm('確定要清空目前所有的選手報名名單嗎？')) {
    queuePlayers.value = [];
    saveToLocalStorage();
  }
};

// --- 📊 清除對戰紀錄封裝函式 ---
const clearMatchHistories = () => {
  if (confirm('確定要清除所有對戰紀錄嗎？')) {
    matchHistories.value = [];
    saveToLocalStorage();
  }
};

// --- 8. 自動配對與場地管理 ---
const hasAvoidConflict = (courtPlayers: Player[], candidate: Player): boolean => {
  const candidateAvoids = candidate.avoidIds || [];
  for (const p of courtPlayers) {
    if (candidateAvoids.includes(p.id) || (p.avoidIds && p.avoidIds.includes(candidate.id))) {
      return true;
    }
    if (getPairMatchCount(candidate.name, p.name) >= 2) {
      return true;
    }
  }
  return false;
};

const isPreferredLevel = (courtPlayers: Player[], targetPlayer: Player): boolean => {
  if (hasAvoidConflict(courtPlayers, targetPlayer)) return false; 
  if (courtPlayers.length === 0) return true;
  const targetLevel = Number(targetPlayer.level);
  if (isNaN(targetLevel)) return true;
  return courtPlayers.every(p => Math.abs(Number(p.level) - targetLevel) <= 2);
};

const fillZoneForCourt = (court: Court, targetZone: 'players' | 'nextPlayers', activeQueue: Player[]) => {
  const tryAddPlayerAndPartner = (candidate: Player, strictLevel: boolean): boolean => {
    if (court[targetZone].length >= 4) return false;
    if (strictLevel && !isPreferredLevel(court[targetZone], candidate)) return false;
    if (!strictLevel && hasAvoidConflict(court[targetZone], candidate)) return false; 

    const partner = activeQueue.find(p => String(p.id) === String(candidate.partnerId));

    if (partner) {
      if (court[targetZone].length > 2) return false;
      if (strictLevel && !isPreferredLevel(court[targetZone], partner)) return false;
      if (!strictLevel && hasAvoidConflict(court[targetZone], partner)) return false;

      court[targetZone].push(candidate, partner);

      [candidate, partner].forEach(item => {
        const idxInQueue = queuePlayers.value.findIndex(p => String(p.id) === String(item.id));
        if (idxInQueue !== -1) queuePlayers.value.splice(idxInQueue, 1);

        const idxInActive = activeQueue.findIndex(p => String(p.id) === String(item.id));
        if (idxInActive !== -1) activeQueue.splice(idxInActive, 1);
      });
      return true;
    } else {
      court[targetZone].push(candidate);

      const idxInQueue = queuePlayers.value.findIndex(p => String(p.id) === String(candidate.id));
      if (idxInQueue !== -1) queuePlayers.value.splice(idxInQueue, 1);

      const idxInActive = activeQueue.findIndex(p => String(p.id) === String(candidate.id));
      if (idxInActive !== -1) activeQueue.splice(idxInActive, 1);

      return true;
    }
  };

  let i = 0;
  while (court[targetZone].length < 4 && i < activeQueue.length) {
    const candidate = activeQueue[i];
    if (candidate) {
      const added = tryAddPlayerAndPartner(candidate, true);
      if (!added) i++;
    } else {
      i++;
    }
  }

  i = 0;
  while (court[targetZone].length < 4 && i < activeQueue.length) {
    const candidate = activeQueue[i];
    if (candidate) {
      const added = tryAddPlayerAndPartner(candidate, false);
      if (!added) i++;
    } else {
      i++;
    }
  }
};

const fillCourtAuto = (court: Court) => {
  const activeQueue = filteredQueuePlayers.value.filter(p => String(p.status) !== '0' && !isPlayerLimitReached(p));
  activeQueue.sort((a, b) => a.playedCount - b.playedCount);

  if (court.status === 'idle' && court.players.length < 4) {
    fillZoneForCourt(court, 'players', activeQueue);
  }
  if (court.nextPlayers.length < 4) {
    fillZoneForCourt(court, 'nextPlayers', activeQueue);
  }
  saveToLocalStorage();
};

const autoAssignAllCourts = () => {
  const activeQueue = filteredQueuePlayers.value.filter(p => String(p.status) !== '0' && !isPlayerLimitReached(p));
  if (activeQueue.length === 0) return alert('目前佇列中沒有可安排上場的球員！');
  
  activeQueue.sort((a, b) => a.playedCount - b.playedCount);

  courts.value.forEach(court => {
    if (court.status === 'idle' && court.players.length < 4) {
      fillZoneForCourt(court, 'players', activeQueue);
    }
  });

  courts.value.forEach(court => {
    if (court.nextPlayers.length < 4) {
      fillZoneForCourt(court, 'nextPlayers', activeQueue);
    }
  });

  saveToLocalStorage();
};

const updateCourtCountFromBoard = () => {
  initCourts(sessionConfig.value.courtCount);
  saveToLocalStorage();
};

const addPlayerToAvailableCourt = (player: Player) => {
  if (isPlayerLimitReached(player)) {
    return alert(`【${player.name}】已打滿個人上限 (${getPlayerMaxGames(player)} 場)，無法繼續上場！`);
  }

  const partner = getPartner(player);
  const neededSlots = partner ? 2 : 1;

  if (partner && isPlayerLimitReached(partner)) {
    return alert(`搭檔【${partner.name}】已打滿個人上限，無法一起上場！`);
  }

  const targetCourt = courts.value.find(c => c.status === 'idle' && (4 - c.players.length) >= neededSlots && isPreferredLevel(c.players, player))
    || courts.value.find(c => c.status === 'idle' && (4 - c.players.length) >= neededSlots)
    || courts.value.find(c => (4 - c.nextPlayers.length) >= neededSlots && isPreferredLevel(c.nextPlayers, player))
    || courts.value.find(c => (4 - c.nextPlayers.length) >= neededSlots);

  if (!targetCourt) return alert(`目前無場地有足夠空位 (${neededSlots}人)！`);

  const zone = (targetCourt.status === 'idle' && (4 - targetCourt.players.length) >= neededSlots) ? 'players' : 'nextPlayers';
  
  const index = queuePlayers.value.findIndex(p => String(p.id) === String(player.id));
  if (index !== -1) {
    queuePlayers.value.splice(index, 1);
    targetCourt[zone].push(player);
  }

  if (partner) {
    const partnerIndex = queuePlayers.value.findIndex(p => String(p.id) === String(partner.id));
    if (partnerIndex !== -1) {
      queuePlayers.value.splice(partnerIndex, 1);
      targetCourt[zone].push(partner);
    }
  }

  saveToLocalStorage();
};

const startMatch = (court: Court) => {
  if (court.players.length !== 4) {
    return alert(`【${court.name}】目前有 ${court.players.length} 人，必須湊齊 4 人才能開始比賽！`);
  }
  court.status = 'playing';
  court.startTime = Date.now();
  court.elapsedSeconds = 0;
};

// 🏁 結束比賽：原本在場上打完的人送回等待佇列，下局預備區的人遞補上場，並清空預備區
const finishMatch = (courtId: number) => {
  const court = courts.value.find(c => c.id === courtId);
  if (!court) return;

  const playerNames = court.players.map(p => p.name);
  
  // 記錄對戰歷史
  matchHistories.value.unshift({
    id: 'm_' + Date.now() + '_' + court.id,
    timestamp: Date.now(),
    courtName: court.name,
    playerNames: [...playerNames]
  });

  // 1. 將剛剛打完比賽的現正對局球員 playedCount + 1，並放回等待佇列
  court.players.forEach(p => {
    p.playedCount += 1;
    p.lastMatchPeers = playerNames.filter(name => name !== p.name);
    p.queueEnteredAt = Date.now(); 
    if (!queuePlayers.value.some(existing => String(existing.id) === String(p.id))) {
      queuePlayers.value.push(p);
    }
  });

  // 2. 將原本在「下局預備 (nextPlayers)」的球員遞補升格成為「現正對局 (players)」
  court.players = [...court.nextPlayers];
  
  // 3. 清空下局預備區
  court.nextPlayers = []; 
  
  // 4. 重置場地狀態為準備中 (idle)
  court.status = 'idle';
  court.startTime = null;
  court.elapsedSeconds = 0;

  saveToLocalStorage();
};

const clearAllCourts = () => {
  if (courts.value.some(c => c.status === 'playing') && !confirm('有場地正進行比賽，確定要清空嗎？')) return;

  courts.value.forEach(court => {
    court.players.concat(court.nextPlayers).forEach(p => {
      p.queueEnteredAt = Date.now();
      if (!queuePlayers.value.some(q => String(q.id) === String(p.id))) {
        queuePlayers.value.push(p);
      }
    });
    court.players = [];
    court.nextPlayers = [];
    court.status = 'idle';
    court.startTime = null;
    court.elapsedSeconds = 0;
  });

  saveToLocalStorage();
};

const dragOptions = computed(() => ({
  animation: 200,
  group: "badminton-app",
  ghostClass: "ghost",
  delay: 0,
  touchStartThreshold: 5,
  forceFallback: true,
  fallbackClass: "sortable-fallback"
}));

const getLevelClass = (level: string | number) => {
  const num = Number(level);
  return (!isNaN(num) && num >= 1 && num <= 15) ? `level-${num}` : '';
};

const getGenderText = (gender: string) => {
  if (gender === 'M') return '♂';
  if (gender === 'F') return '♀';
  return '';
};
</script>

<template>
  <div class="app-main-layout">
    <!-- 畫面 1: 今日開場參數與批次報名設定 -->
    <div v-if="currentView === 'setup'" class="setup-container">
      <div class="setup-grid">
        <!-- 左側：活動參數設定 -->
        <div class="setup-card">
          <div class="setup-header">
            <h2>🏸 1. 今日開場參數設定</h2>
            <p>設定場地數、預計總人數、個人單時段上限與開放時段</p>
          </div>

          <div class="setup-form">
            <div class="form-item">
              <label for="setupCourts">🏟️ 開現場地數量：</label>
              <div class="input-with-unit">
                <input id="setupCourts" type="number" min="1" max="16" v-model.number="sessionConfig.courtCount" class="setup-input" />
                <span>個場地</span>
              </div>
            </div>

            <div class="form-item">
              <label for="setupMaxPlayers">👥 今日預計總人數：</label>
              <div class="input-with-unit">
                <input id="setupMaxPlayers" type="number" min="1" max="100" v-model.number="sessionConfig.maxPlayersCount" class="setup-input" />
                <span>人</span>
              </div>
            </div>

            <div class="form-item">
              <label for="setupMaxGames">🎾 單一時段最多打幾場：</label>
              <div class="input-with-unit">
                <input id="setupMaxGames" type="number" min="1" max="20" v-model.number="sessionConfig.maxGamesPerPlayer" class="setup-input" />
                <span>場/時段</span>
              </div>
              <small class="hint-text">💡 報名 2 個時段的選手，總上限將自動倍增。</small>
            </div>

            <div class="form-item">
              <div class="slot-label-row">
                <label>⏰ 今日開放時段：</label>
                <button class="btn-reset-slots" @click="resetTimeSlotsToDefault" title="歸為預設開場時段">🔄 恢復預設時段</button>
              </div>
              <div class="slots-editor">
                <div v-for="(slot, idx) in sessionConfig.timeSlots" :key="idx" class="slot-tag">
                  <span>{{ slot }}</span>
                  <button class="btn-remove-slot" @click="removeTimeSlot(idx)">✕</button>
                </div>
              </div>
              <div class="add-slot-row">
                <input type="text" v-model="newSlotInput" placeholder="例: 18:00-20:00" class="setup-input small-input" @keyup.enter="addTimeSlot" />
                <button class="btn-add-slot" @click="addTimeSlot">➕ 新增時段</button>
              </div>
            </div>
          </div>

          <div class="setup-actions">
            <button class="btn-start-session" @click="startSession">🚀 完成設定，進入排點看板</button>
          </div>
        </div>

        <!-- 中間：批次報名匯入工具 -->
        <div class="setup-card">
          <div class="setup-header">
            <h2>📋 2. 選手批次報名</h2>
            <p>可直接複製 LINE 名單貼上，或讀取 CSV 檔案</p>
          </div>

          <div class="batch-defaults">
            <div class="default-item">
              <label>預設性別：</label>
              <select v-model="batchDefaultGender" class="small-select">
                <option value="-">⚪ 無 / 不指定</option>
                <option value="M">♂ 男</option>
                <option value="F">♀ 女</option>
              </select>
            </div>
            <div class="default-item">
              <label>預設等級：</label>
              <select v-model="batchDefaultLevel" class="small-select">
                <option v-for="n in 15" :key="n" :value="String(n)">{{ n }} 級</option>
              </select>
            </div>
          </div>

          <div class="batch-sessions-select">
            <label class="session-label">預設帶入時段：</label>
            <div class="checkbox-group">
              <label v-for="slot in sessionConfig.timeSlots" :key="slot" class="checkbox-item">
                <input type="checkbox" :value="slot" v-model="batchDefaultSessions" />
                <span>{{ slot }}</span>
              </label>
            </div>
          </div>

          <div class="form-item" style="margin-top: 10px;">
            <label>快速貼上名單：</label>
            <textarea 
              v-model="batchText" 
              rows="6" 
              class="batch-textarea" 
              placeholder="範例格式：
01.威力 11
02.阿布 13
03.Romy 8 🚺"
            ></textarea>
            <button class="btn-import-text" @click="parseAndImportText">📥 批次匯入文字名單</button>
          </div>

          <hr class="divider" />

          <div class="form-item">
            <label>或 上傳 CSV 檔案匯入：</label>
            <input type="file" accept=".csv" @change="handleFileUpload" class="file-input" />
          </div>
        </div>

        <!-- 右側：目前已報名名單預覽區 -->
        <div class="setup-card preview-card">
          <div class="setup-header preview-header">
            <h3>👥 已報名選手名單 ({{ queuePlayers.length }} / {{ sessionConfig.maxPlayersCount }} 人)</h3>
            <button class="btn-clear-all" @click="clearAllPlayersInSetup" v-if="queuePlayers.length > 0">🗑 清空</button>
          </div>

          <div v-if="queuePlayers.length === 0" class="empty-tip">目前尚無選手報名，請使用中間批次報名工具。</div>
          <div v-else class="preview-player-tags">
            <div v-for="p in queuePlayers" :key="p.id" class="preview-tag">
              <span v-if="getGenderText(p.gender)" class="p-gender" :class="p.gender === 'M' ? 'male' : 'female'">{{ getGenderText(p.gender) }}</span>
              <span class="p-name">{{ p.name }}</span>
              <span class="p-level">{{ p.level }}級</span>
              <span class="p-partner" v-if="getPartner(p)">🔗 {{ getPartner(p)?.name }}</span>
              <button class="btn-remove-tag" @click="removeSinglePlayerInSetup(p.id)">✕</button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 畫面 2: 排點看板主畫面 -->
    <template v-else>
      <header class="app-header">
        <div class="header-content">
          <div class="header-top-row">
            <h1 class="app-title">
              🏸 羽球場邊排點看板 
              <span class="mode-badge" :class="isDev ? 'dev' : 'prod'">
                {{ isDev ? '⚡ 本地開發 API 模式' : '💾 正式離線暫存模式' }}
              </span>
            </h1>

            <div class="session-filter-bar">
              <span class="filter-label">⏰ 時段篩選:</span>
              <button class="btn-session-filter" :class="{ active: currentSessionFilter === 'all' }" @click="currentSessionFilter = 'all'">全部時段</button>
              <button v-for="slot in sortedTimeSlots" :key="slot" class="btn-session-filter" :class="{ active: currentSessionFilter === slot }" @click="currentSessionFilter = slot">
                {{ slot }}
              </button>
            </div>
          </div>

          <div class="header-controls">
            <button class="btn-setup-reconfig" @click="reopenSetup">⚙️ 調整開場設定</button>
            <button class="btn-add-player" @click="openAddModal">➕ 選手報名</button>
            <button class="btn-auto-assign" @click="autoAssignAllCourts">⚡ 一鍵自動排點</button>
            <button class="btn-history-modal" @click="isHistoryModalOpen = true">📊 對戰紀錄 ({{ matchHistories.length }})</button>
            <button class="btn-clear-courts" @click="clearAllCourts" :disabled="isLoading">🗑 清空場地</button>

            <div class="court-selector">
              <label for="courtCount">場地數量:</label>
              <select id="courtCount" v-model="sessionConfig.courtCount" @change="updateCourtCountFromBoard">
                <option v-for="num in [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 16]" :key="num" :value="num">{{ num }} 個場地</option>
              </select>
            </div>
          </div>
        </div>
      </header>

      <main class="app-content-wrapper">
        <!-- 左側場地區 -->
        <section class="courts-section">
          <div class="section-header">
            <h2>🏸 場地動態 ({{ courts.length }} 個場地)</h2>
          </div>

          <div class="courts-grid">
            <div v-for="court in courts" :key="court.id" class="court-card" :class="court.status">
              <div class="court-header">
                <div class="title-with-status">
                  <h3>{{ court.name }}</h3>
                  <span class="status-badge" :class="court.status">
                    {{ court.status === 'playing' ? '🔥 比賽中' : '⏳ 準備中' }}
                  </span>
                </div>
                <div class="court-header-right">
                  <span v-if="court.status === 'playing'" class="match-timer">⏱️ {{ formatTime(court.elapsedSeconds) }}</span>
                  <span class="player-count" :class="{ 'full': court.players.length === 4 }">{{ court.players.length }} / 4人</span>
                </div>
              </div>

              <!-- 現正對局 -->
              <div class="zone-label">
                <span>🏸 現正對局</span>
              </div>
              <draggable class="player-list court-player-list"
                         v-model="court.players"
                         v-bind="dragOptions"
                         :disabled="court.status === 'playing'"
                         :set-data="false"
                         @add="handlePlayerAddedToCourt">
                <div v-for="player in court.players" :key="player.id" class="player-card touch-action-none">
                  <div class="player-info">
                    <span v-if="getGenderText(player.gender)" class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ getGenderText(player.gender) }}</span>
                    <span class="player-name">{{ player.name }}</span>
                    <span v-if="getPartner(player)" class="partner-badge">🔗 {{ getPartner(player)?.name }}</span>
                  </div>
                  <div class="player-stats">
                    <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                    <span class="played-tag" :class="{ 'limit-reached': isPlayerLimitReached(player) }">
                      {{ player.playedCount }} / {{ getPlayerMaxGames(player) }}場
                    </span>
                  </div>
                </div>
              </draggable>

              <!-- 下局預備 -->
              <div class="zone-label next-zone-label">
                <span>📋 下局預備 ({{ court.nextPlayers.length }}/4)</span>
              </div>
              <draggable class="player-list court-player-list next-players-list"
                         v-model="court.nextPlayers"
                         v-bind="dragOptions"
                         :set-data="false"
                         @add="handlePlayerAddedToCourt">
                <div v-for="player in court.nextPlayers" :key="player.id" class="player-card touch-action-none is-next-up">
                  <div class="player-info">
                    <span v-if="getGenderText(player.gender)" class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ getGenderText(player.gender) }}</span>
                    <span class="player-name">{{ player.name }}</span>
                    <span v-if="getPartner(player)" class="partner-badge">🔗 {{ getPartner(player)?.name }}</span>
                  </div>
                  <div class="player-stats">
                    <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                    <span class="played-tag" :class="{ 'limit-reached': isPlayerLimitReached(player) }">
                      {{ player.playedCount }} / {{ getPlayerMaxGames(player) }}場
                    </span>
                  </div>
                </div>
              </draggable>

              <div class="court-actions">
                <button v-if="court.status === 'idle'" class="btn-start-match" @click="startMatch(court)">▶ 開始比賽</button>
                <button v-if="court.status === 'playing'" class="btn-finish-match" @click="finishMatch(court.id)" :disabled="isLoading">🏁 結束此局</button>
              </div>
            </div>
          </div>
        </section>

        <!-- 右側等待佇列 -->
        <section class="queue-section">
          <div class="section-header">
            <div class="header-title-group">
              <h2>⏳ 等待佇列 ({{ filteredQueuePlayers.length }} / {{ queuePlayers.length }} 人)</h2>
              <button class="btn-toggle-batch" :class="{ 'active': isBatchMode }" @click="toggleBatchMode">
                {{ isBatchMode ? '✕ 取消選擇' : '☑ 批次選擇' }}
              </button>
            </div>

            <div class="group-mode-switch">
              <span class="switch-label">分區方式：</span>
              <button class="btn-mode-tab" :class="{ active: queueGroupMode === 'level' }" @click="toggleGroupMode('level')">
                🏆 按等級
              </button>
              <button class="btn-mode-tab" :class="{ active: queueGroupMode === 'playedCount' }" @click="toggleGroupMode('playedCount')">
                🔢 按場次
              </button>
              <button class="btn-mode-tab" :class="{ active: queueGroupMode === 'resting' }" @click="toggleGroupMode('resting')">
                💤 休息中
              </button>
            </div>

            <div v-if="isBatchMode" class="batch-actions-bar">
              <button class="btn-batch-select-all" @click="toggleSelectAll">
                {{ selectedPlayerIds.length === currentTabPlayers.length && currentTabPlayers.length > 0 ? '全不選' : '全選本頁' }}
              </button>
              <span class="selected-count">已選 {{ selectedPlayerIds.length }} 人</span>
              <div class="batch-btns">
                <button v-if="queueGroupMode === 'resting'" class="btn-batch-active" :disabled="selectedPlayerIds.length === 0" @click="updateBatchStatus('1')">🟢 設為上場</button>
                <button v-else class="btn-batch-rest" :disabled="selectedPlayerIds.length === 0" @click="updateBatchStatus('0')">💤 設為休息</button>

                <button class="btn-batch-reset" :disabled="selectedPlayerIds.length === 0" @click="batchResetPlayedCount">🔄 場次歸零</button>
                <button class="btn-batch-delete" :disabled="selectedPlayerIds.length === 0" @click="batchDeletePlayers">🗑️ 刪除</button>
              </div>
            </div>
            <span v-else class="header-tip">💡 點擊選手卡片可查看等待時間、上局夥伴或設定錯開名單</span>
          </div>

          <div class="queue-container">
            <div v-for="group in groupedQueuePlayers" :key="group.key" class="queue-group">
              <div class="group-header clickable" @click="toggleGroupCollapse(group.key)">
                <div class="group-title">
                  <span class="collapse-icon">{{ collapsedGroups[group.key] ? '►' : '▼' }}</span>
                  <span>{{ group.title }}</span>
                </div>
                <span class="group-count">{{ group.players.length }} 人</span>
              </div>

              <draggable v-show="!collapsedGroups[group.key]"
                         class="player-list queue-player-list touch-action-none"
                         v-model="group.players"
                         v-bind="dragOptions"
                         :disabled="isBatchMode"
                         :set-data="false"
                         @add="handlePlayerAddedToQueue">
                <div v-for="player in group.players"
                     :key="player.id"
                     class="player-card clickable"
                     :class="{ 
                       'is-selected': selectedPlayerIds.includes(player.id), 
                       'is-resting': String(player.status) === '0',
                       'is-completed': isPlayerLimitReached(player)
                     }"
                     @click="isBatchMode ? toggleSelectPlayer(player) : openEditModal(player)">

                  <div v-if="isBatchMode" class="checkbox-wrapper">
                    <input type="checkbox" 
                           :checked="selectedPlayerIds.includes(player.id)" 
                           @click.stop="toggleSelectPlayer(player)" />
                  </div>

                  <div class="player-info-container">
                    <div class="player-info">
                      <span v-if="getGenderText(player.gender)" class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ getGenderText(player.gender) }}</span>
                      <span class="player-name">{{ player.name }}</span>
                      <span v-if="getPartner(player)" class="partner-badge">🔗 {{ getPartner(player)?.name }}</span>
                      <span v-if="String(player.status) === '0'" class="resting-badge">💤 休息</span>
                      <span v-else-if="isPlayerLimitReached(player)" class="completed-badge">🏁 完賽</span>
                    </div>

                    <!-- ⏳ 顯示等待時間與上局夥伴資訊 -->
                    <div class="player-extra-meta">
                      <span class="wait-time-badge" :class="{ 'warning-wait': getWaitSeconds(player) > 900 }">
                        ⏳ 等待: {{ formatWaitTime(getWaitSeconds(player)) }}
                      </span>
                      <span v-if="player.lastMatchPeers && player.lastMatchPeers.length > 0" class="last-peers-badge" :title="'上局同場: ' + player.lastMatchPeers.join(', ')">
                        👥 上局: {{ player.lastMatchPeers.join(', ') }}
                      </span>
                    </div>
                  </div>

                  <div class="player-actions-right">
                    <div class="player-stats">
                      <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                      <span class="played-tag" :class="{ 'limit-reached': isPlayerLimitReached(player) }">
                        {{ player.playedCount }}/{{ getPlayerMaxGames(player) }}
                      </span>
                    </div>
                    <button v-if="!isBatchMode && String(player.status) !== '0' && !isPlayerLimitReached(player)" class="btn-quick-add" title="快速排入" @click.stop="addPlayerToAvailableCourt(player)">➕</button>
                  </div>
                </div>
              </draggable>
            </div>
          </div>
        </section>
      </main>

      <!-- Modal: 新增選手 -->
      <div v-if="isAddModalOpen" class="modal-overlay" @click.self="closeAddModal">
        <div class="modal-card">
          <div class="modal-header">
            <h3>➕ 新增選手報名</h3>
            <button class="btn-close" @click="closeAddModal">✕</button>
          </div>
          <div class="modal-body">
            <div class="form-group">
              <label>姓名：</label>
              <input type="text" v-model="newPlayer.name" placeholder="請輸入選手姓名" class="form-input" />
            </div>
            <div class="form-group">
              <label>性別：</label>
              <select v-model="newPlayer.gender" class="form-select">
                <option value="-">⚪ 無 / 不指定</option>
                <option value="M">♂ 男 (Male)</option>
                <option value="F">♀ 女 (Female)</option>
              </select>
            </div>
            <div class="form-group">
              <label>實力等級：</label>
              <select v-model="newPlayer.level" class="form-select">
                <option v-for="n in 15" :key="n" :value="String(n)">{{ n }} 級</option>
              </select>
            </div>
          </div>
          <div class="modal-footer">
            <button class="btn-cancel" @click="closeAddModal">取消</button>
            <button class="btn-save" @click="saveNewPlayer">確認報名</button>
          </div>
        </div>
      </div>

      <!-- Modal: 編輯選手 (含錯開設定與搭檔綁定) -->
      <div v-if="isEditModalOpen && editingPlayer" class="modal-overlay" @click.self="closeEditModal">
        <div class="modal-card">
          <div class="modal-header">
            <h3>✏️ 編輯選手資料 ({{ editingPlayer.name }})</h3>
            <button class="btn-close" @click="closeEditModal">✕</button>
          </div>
          <div class="modal-body">
            <div class="form-group">
              <label>姓名：</label>
              <input type="text" v-model="editingPlayer.name" class="form-input" />
            </div>
            
            <div class="form-group">
              <label>👥 綁定搭檔 (一起上場)：</label>
              <select v-model="editingPlayer.partnerId" class="form-select">
                <option :value="null">-- 無搭檔 (單人排隊) --</option>
                <option 
                  v-for="p in queuePlayers.filter(x => String(x.id) !== String(editingPlayer?.id))" 
                  :key="p.id" 
                  :value="p.id">
                  {{ p.name }} ({{ p.level }}級)
                </option>
              </select>
            </div>

            <!-- ⛔ 避開特定球員設定 -->
            <div class="form-group">
              <label>⛔ 避開特定球員 (不同場，可多選)：</label>
              <div class="checkbox-group avoid-checkboxes">
                <label v-for="p in queuePlayers.filter(x => String(x.id) !== String(editingPlayer?.id))" :key="p.id" class="checkbox-item">
                  <input type="checkbox" :value="p.id" v-model="editingPlayer.avoidIds" />
                  <span>{{ p.name }} ({{ p.level }}級)</span>
                </label>
              </div>
              <small class="hint-text">💡 勾選後，系統在自動排點或快速加入時，絕不會將兩人排在同一場（註：同場達 2 次以上也會自動避開）。</small>
            </div>

            <div class="form-group">
              <label>性別：</label>
              <select v-model="editingPlayer.gender" class="form-select">
                <option value="-">⚪ 無 / 不指定</option>
                <option value="M">♂ 男</option>
                <option value="F">♀ 女</option>
              </select>
            </div>
            <div class="form-group">
              <label>實力等級：</label>
              <select v-model="editingPlayer.level" class="form-select">
                <option v-for="n in 15" :key="n" :value="String(n)">{{ n }} 級</option>
              </select>
            </div>
            <div class="form-group">
              <label>已打場數：</label>
              <input type="number" min="0" v-model.number="editingPlayer.playedCount" class="form-input" />
            </div>
            <div class="form-group">
              <label>出勤狀態：</label>
              <select v-model="editingPlayer.status" class="form-select">
                <option value="1">🟢 上場排隊中</option>
                <option value="0">🔴 休息請假中</option>
              </select>
            </div>
          </div>
          <div class="modal-footer">
            <button class="btn-cancel" @click="closeEditModal">取消</button>
            <button class="btn-save" @click="savePlayerEdit">儲存變更</button>
          </div>
        </div>
      </div>

      <!-- Modal: 對戰紀錄查詢 -->
      <div v-if="isHistoryModalOpen" class="modal-overlay" @click.self="isHistoryModalOpen = false">
        <div class="modal-card history-modal-card">
          <div class="modal-header">
            <h3>📊 全局對戰紀錄 (共 {{ matchHistories.length }} 場)</h3>
            <button class="btn-close" @click="isHistoryModalOpen = false">✕</button>
          </div>
          <div class="modal-body history-modal-body">
            <div v-if="matchHistories.length === 0" class="empty-tip">目前尚無任何已結束的比賽紀錄。</div>
            <div v-else class="history-list">
              <div v-for="record in matchHistories" :key="record.id" class="history-item">
                <div class="history-item-top">
                  <span class="h-court">{{ record.courtName }}</span>
                  <span class="h-time">{{ new Date(record.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) }}</span>
                </div>
                <div class="h-players">
                  <span class="h-player-tag" v-for="name in record.playerNames" :key="name">{{ name }}</span>
                </div>
              </div>
            </div>
          </div>
          <div class="modal-footer">
            <button class="btn-cancel" @click="isHistoryModalOpen = false">關閉</button>
            <button class="btn-save btn-danger-sub" @click="clearMatchHistories">清空紀錄</button>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.app-main-layout {
  min-height: 100vh;
  min-height: 100dvh;
  background-color: #f8fafc;
  font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  color: #334155;
  box-sizing: border-box;
}

.touch-action-none { touch-action: none; }
.clickable { cursor: pointer; }

.setup-container { display: flex; justify-content: center; padding: 24px 20px; max-width: 1440px; margin: 0 auto; }
.setup-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 20px; width: 100%; }
.setup-card { background: #ffffff; border-radius: 12px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.05); padding: 20px; display: flex; flex-direction: column; border: 1px solid #e2e8f0; }

.preview-card { display: flex; flex-direction: column; max-height: calc(100vh - 80px); }
.setup-header h2, .setup-header h3 { font-size: 1.15rem; color: #1e293b; margin: 0 0 4px 0; }
.setup-header p { color: #64748b; font-size: 0.825rem; margin: 0; }

.setup-form { display: flex; flex-direction: column; gap: 14px; margin-top: 12px; }
.form-item { display: flex; flex-direction: column; gap: 6px; }
.form-item label { font-weight: 600; color: #334155; font-size: 0.85rem; }
.slot-label-row { display: flex; justify-content: space-between; align-items: center; }
.btn-reset-slots { background: none; border: none; color: #2563eb; font-size: 0.75rem; cursor: pointer; text-decoration: underline; }
.input-with-unit { display: flex; align-items: center; gap: 8px; font-size: 0.85rem; color: #64748b; }
.setup-input { flex: 1; padding: 8px 12px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 0.9rem; outline: none; }
.setup-actions { margin-top: 16px; padding-top: 12px; }
.btn-start-session { width: 100%; padding: 12px; background-color: #2563eb; color: #ffffff; font-size: 0.95rem; font-weight: 600; border: none; border-radius: 8px; cursor: pointer; }

.slots-editor { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 4px; }
.slot-tag { display: flex; align-items: center; gap: 4px; background-color: #e0f2fe; color: #0369a1; padding: 4px 10px; border-radius: 12px; font-size: 0.8rem; font-weight: 600; }
.btn-remove-slot { border: none; background: transparent; color: #0284c7; cursor: pointer; font-size: 0.85rem; }
.add-slot-row { display: flex; gap: 8px; }
.small-input { max-width: 160px; }
.btn-add-slot { padding: 6px 12px; background-color: #0284c7; color: white; border: none; border-radius: 6px; font-size: 0.8rem; cursor: pointer; }

.batch-defaults { display: flex; gap: 16px; background-color: #f8fafc; padding: 8px 12px; border-radius: 6px; margin: 12px 0; border: 1px solid #e2e8f0; flex-wrap: wrap; }
.default-item { display: flex; align-items: center; gap: 6px; font-size: 0.8rem; color: #475569; }
.small-select { padding: 4px 8px; border-radius: 4px; border: 1px solid #cbd5e1; background-color: #fff; font-size: 0.8rem; }
.session-label { font-size: 0.8rem; font-weight: 600; color: #475569; }
.checkbox-group { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 6px; }
.checkbox-item { display: flex; align-items: center; gap: 4px; font-size: 0.8rem; color: #334155; cursor: pointer; }

.batch-textarea { width: 100%; padding: 10px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 0.85rem; resize: vertical; outline: none; box-sizing: border-box; }
.btn-import-text { margin-top: 8px; padding: 8px 14px; background-color: #059669; color: #ffffff; border: none; border-radius: 6px; font-weight: 600; font-size: 0.85rem; cursor: pointer; }

.divider { border: none; border-top: 1px dashed #e2e8f0; margin: 14px 0; }
.preview-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }
.btn-clear-all { background-color: #ef4444; color: white; border: none; padding: 4px 10px; border-radius: 4px; font-size: 0.8rem; cursor: pointer; }
.empty-tip { font-size: 0.85rem; color: #94a3b8; padding: 12px 0; }
.preview-player-tags { display: flex; flex-direction: column; gap: 8px; flex: 1; overflow-y: auto; padding: 8px; background-color: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; max-height: 520px; }
.preview-tag { display: flex; align-items: center; justify-content: space-between; background-color: #ffffff; border: 1px solid #cbd5e1; padding: 6px 10px; border-radius: 8px; font-size: 0.825rem; }

.p-gender.male { color: #2563eb; font-weight: bold; }
.p-gender.female { color: #ec4899; font-weight: bold; }
.p-name { font-weight: 600; flex: 1; margin: 0 6px; color: #1e293b; }
.p-level { color: #475569; font-size: 0.75rem; background: #f1f5f9; padding: 1px 5px; border-radius: 4px; }
.p-partner { font-size: 0.72rem; background-color: #fef3c7; color: #92400e; padding: 1px 6px; border-radius: 4px; margin-left: 4px; }
.btn-remove-tag { border: none; background: transparent; color: #94a3b8; cursor: pointer; font-weight: bold; }

.app-header { background: linear-gradient(135deg, #0f172a 0%, #1e293b 100%); color: #ffffff; padding: 12px 16px; position: sticky; top: 0; z-index: 100; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.12); }
.header-content { max-width: 1600px; margin: 0 auto; display: flex; flex-direction: column; gap: 10px; }
.header-top-row { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 10px; }
.app-title { font-size: 1.2rem; margin: 0; display: flex; align-items: center; gap: 8px; font-weight: 700; flex-wrap: wrap; }
.mode-badge { font-size: 0.7rem; padding: 2px 8px; border-radius: 12px; font-weight: 500; }
.mode-badge.dev { background-color: #f59e0b; color: #fff; }
.mode-badge.prod { background-color: #10b981; color: #fff; }

.session-filter-bar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.filter-label { font-size: 0.8rem; color: #94a3b8; }
.btn-session-filter { background: rgba(255, 255, 255, 0.08); color: #cbd5e1; border: 1px solid rgba(255, 255, 255, 0.15); padding: 4px 12px; border-radius: 20px; font-size: 0.78rem; cursor: pointer; }
.btn-session-filter.active { background: #2563eb; color: #ffffff; border-color: #3b82f6; font-weight: 600; }

.header-controls { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.header-controls button { padding: 6px 12px; border-radius: 8px; font-size: 0.8rem; font-weight: 600; border: none; cursor: pointer; display: inline-flex; align-items: center; gap: 4px; min-height: 34px; }
.btn-setup-reconfig { background-color: #334155; color: #f8fafc; }
.btn-add-player { background-color: #059669; color: white; }
.btn-auto-assign { background-color: #d97706; color: white; }
.btn-history-modal { background-color: #7c3aed; color: white; }
.btn-clear-courts { background-color: #dc2626; color: white; }

.court-selector { margin-left: auto; display: flex; align-items: center; gap: 6px; font-size: 0.8rem; color: #cbd5e1; background: rgba(255, 255, 255, 0.06); padding: 4px 8px; border-radius: 8px; border: 1px solid rgba(255, 255, 255, 0.1); min-height: 34px; box-sizing: border-box; }
.court-selector select { padding: 2px 6px; border-radius: 6px; border: 1px solid #475569; background-color: #0f172a; color: white; font-weight: 600; outline: none; }

.app-content-wrapper { max-width: 1600px; margin: 12px auto 0 auto; padding: 0 12px 12px 12px; display: grid; grid-template-columns: minmax(0, 1fr) 340px; gap: 16px; align-items: start; box-sizing: border-box; }
.courts-section { display: flex; flex-direction: column; width: 100%; min-width: 0; max-height: calc(100vh - 120px); overflow-y: auto; padding-right: 4px; }
.queue-section { background: #ffffff; border-radius: 14px; padding: 12px; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.03); border: 1px solid #e2e8f0; display: flex; flex-direction: column; gap: 10px; max-height: calc(100vh - 120px); position: sticky; top: 80px; overflow-y: auto; box-sizing: border-box; }

.section-header { margin-bottom: 6px; display: flex; flex-direction: column; gap: 6px; }
.section-header h2 { font-size: 1.05rem; margin: 0; color: #0f172a; font-weight: 700; }
.header-title-group { display: flex; justify-content: space-between; align-items: center; }
.header-tip { font-size: 0.725rem; color: #64748b; }

.courts-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 12px; }
.court-card { background: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; padding: 12px; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.03); display: flex; flex-direction: column; gap: 8px; }
.court-card.playing { border-color: #3b82f6; box-shadow: 0 6px 16px rgba(59, 130, 246, 0.12); }
.court-header { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #f1f5f9; padding-bottom: 6px; }
.title-with-status { display: flex; align-items: center; gap: 6px; }
.court-header h3 { margin: 0; font-size: 1rem; color: #0f172a; font-weight: 700; }
.match-timer { font-size: 0.75rem; font-weight: 700; color: #1d4ed8; background-color: #eff6ff; padding: 2px 6px; border-radius: 6px; border: 1px solid #bfdbfe; }
.player-count { font-size: 0.725rem; color: #64748b; background-color: #f1f5f9; padding: 2px 6px; border-radius: 6px; font-weight: 600; }
.player-count.full { background-color: #dcfce7; color: #15803d; }
.status-badge { font-size: 0.7rem; padding: 2px 6px; border-radius: 6px; background: #f1f5f9; color: #64748b; font-weight: 600; }
.status-badge.playing { background: #dbeafe; color: #1d4ed8; }

.zone-label { font-size: 0.7rem; font-weight: 700; color: #475569; display: flex; align-items: center; gap: 4px; }
.next-zone-label { margin-top: 2px; color: #64748b; }

.player-list { min-height: 70px; background-color: #f8fafc; border: 1.5px dashed #cbd5e1; border-radius: 8px; padding: 5px; display: flex; flex-direction: column; gap: 5px; }
.player-card { background: #ffffff; border: 1px solid #e2e8f0; border-radius: 6px; padding: 6px 8px; display: flex; justify-content: space-between; align-items: center; box-shadow: 0 1px 2px rgba(0, 0, 0, 0.03); cursor: grab; user-select: none; }
.player-card.is-next-up { background-color: #fdfdfd; border-style: dashed; }
.player-card.is-selected { border-color: #2563eb; background-color: #eff6ff; }
.player-card.is-resting { opacity: 0.85; background-color: #fffbeb; border-left: 3px solid #f59e0b; }
.player-card.is-completed { opacity: 0.75; background-color: #f8fafc; border-left: 3px solid #ef4444; }

.player-info-container { display: flex; flex-direction: column; gap: 2px; flex: 1; }
.player-info { display: flex; align-items: center; gap: 5px; font-size: 0.825rem; font-weight: 600; flex-wrap: wrap; }
.player-gender.male { color: #2563eb; }
.player-gender.female { color: #ec4899; }
.player-name { color: #1e293b; }

.player-extra-meta { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.wait-time-badge { font-size: 0.65rem; background-color: #f1f5f9; color: #475569; padding: 1px 5px; border-radius: 4px; font-weight: 600; }
.wait-time-badge.warning-wait { background-color: #fee2e2; color: #dc2626; }
.last-peers-badge { font-size: 0.65rem; background-color: #f3e8ff; color: #7e22ce; padding: 1px 5px; border-radius: 4px; }

.partner-badge { font-size: 0.65rem; background-color: #fef3c7; color: #92400e; padding: 1px 4px; border-radius: 4px; }
.resting-badge { font-size: 0.65rem; background-color: #fef3c7; color: #b45309; padding: 1px 4px; border-radius: 4px; }
.completed-badge { background-color: #fee2e2; color: #dc2626; font-size: 0.65rem; padding: 1px 4px; border-radius: 4px; font-weight: 600; }

.player-stats { display: flex; align-items: center; gap: 4px; }
.level-tag { background-color: #f1f5f9; color: #334155; font-size: 0.7rem; padding: 1px 4px; border-radius: 4px; font-weight: 700; }
.played-tag { font-size: 0.68rem; color: #64748b; background: #f1f5f9; padding: 1px 4px; border-radius: 4px; }
.played-tag.limit-reached { background-color: #ef4444; color: #ffffff; font-weight: bold; }

.player-actions-right { display: flex; align-items: center; gap: 4px; }
.btn-quick-add { background: #dbeafe; color: #1d4ed8; border: none; border-radius: 4px; width: 24px; height: 24px; display: flex; align-items: center; justify-content: center; font-size: 0.8rem; cursor: pointer; padding: 0; }

.court-actions { margin-top: auto; padding-top: 2px; }
.btn-start-match, .btn-finish-match { width: 100%; padding: 8px; border-radius: 6px; border: none; font-weight: 700; font-size: 0.825rem; cursor: pointer; }
.btn-start-match { background-color: #16a34a; color: white; }
.btn-finish-match { background-color: #dc2626; color: white; }

.btn-toggle-batch { padding: 3px 6px; font-size: 0.725rem; border: 1px solid #cbd5e1; background: #f8fafc; color: #475569; border-radius: 4px; cursor: pointer; font-weight: 600; }
.btn-toggle-batch.active { background-color: #2563eb; color: #ffffff; border-color: #2563eb; }

.group-mode-switch { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; }
.switch-label { font-size: 0.75rem; color: #64748b; }
.btn-mode-tab { padding: 2px 6px; font-size: 0.725rem; border: 1px solid #cbd5e1; background: #f8fafc; color: #475569; border-radius: 4px; cursor: pointer; }
.btn-mode-tab.active { background: #0f172a; color: white; border-color: #0f172a; }

.batch-actions-bar { display: flex; flex-direction: column; gap: 6px; background-color: #f1f5f9; padding: 6px; border-radius: 6px; }
.selected-count { font-size: 0.725rem; font-weight: 600; color: #2563eb; }
.btn-batch-select-all { align-self: flex-start; padding: 2px 5px; font-size: 0.7rem; border: 1px solid #cbd5e1; border-radius: 4px; background: #fff; cursor: pointer; }
.batch-btns { display: flex; gap: 4px; flex-wrap: wrap; }
.batch-btns button { padding: 3px 6px; font-size: 0.7rem; border: none; border-radius: 4px; cursor: pointer; font-weight: 600; }
.btn-batch-active { background-color: #10b981; color: white; }
.btn-batch-rest { background-color: #f59e0b; color: white; }
.btn-batch-reset { background-color: #6366f1; color: white; }
.btn-batch-delete { background-color: #ef4444; color: white; }

.queue-container { display: flex; flex-direction: column; gap: 8px; overflow-y: auto; padding-right: 2px; flex: 1; }
.queue-group { display: flex; flex-direction: column; }
.group-header { display: flex; justify-content: space-between; align-items: center; padding: 5px 8px; background-color: #f1f5f9; border-radius: 6px; font-size: 0.8rem; font-weight: bold; margin-bottom: 4px; color: #334155; user-select: none; }
.group-title { display: flex; align-items: center; gap: 4px; }
.collapse-icon { font-size: 0.6rem; color: #64748b; }
.group-count { font-size: 0.7rem; color: #64748b; font-weight: normal; }
.checkbox-wrapper { margin-right: 4px; display: flex; align-items: center; }

.modal-overlay { position: fixed; top: 0; left: 0; right: 0; bottom: 0; background-color: rgba(15, 23, 42, 0.6); backdrop-filter: blur(4px); display: flex; justify-content: center; align-items: center; z-index: 1000; padding: 16px; box-sizing: border-box; }
.modal-card { background: #ffffff; border-radius: 14px; width: 100%; max-width: 460px; padding: 18px; box-shadow: 0 20px 25px -5px rgba(0, 0, 0, 0.1); max-height: 90vh; overflow-y: auto; box-sizing: border-box; }
.history-modal-card { max-width: 600px; }
.history-modal-body { max-height: 400px; overflow-y: auto; }

.modal-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; border-bottom: 1px solid #f1f5f9; padding-bottom: 6px; }
.modal-header h3 { margin: 0; font-size: 1rem; color: #0f172a; }
.btn-close { border: none; background: transparent; font-size: 1rem; color: #94a3b8; cursor: pointer; }
.modal-body { display: flex; flex-direction: column; gap: 10px; }
.form-group { display: flex; flex-direction: column; gap: 4px; }
.form-group label { font-size: 0.8rem; font-weight: 600; color: #334155; }
.hint-text { font-size: 0.7rem; color: #64748b; }

.form-input, .form-select { padding: 6px 10px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 0.85rem; outline: none; background-color: #fff; }
.avoid-checkboxes { max-height: 120px; overflow-y: auto; border: 1px solid #cbd5e1; padding: 6px; border-radius: 6px; background: #f8fafc; }

.history-list { display: flex; flex-direction: column; gap: 8px; }
.history-item { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 8px 12px; display: flex; flex-direction: column; gap: 4px; }
.history-item-top { display: flex; justify-content: space-between; font-size: 0.75rem; color: #64748b; font-weight: 600; }
.h-players { display: flex; gap: 6px; flex-wrap: wrap; }
.h-player-tag { background: #e0f2fe; color: #0369a1; padding: 2px 8px; border-radius: 6px; font-size: 0.8rem; font-weight: 600; }

.modal-footer { margin-top: 14px; display: flex; justify-content: flex-end; gap: 8px; }
.btn-cancel, .btn-save { padding: 6px 12px; border-radius: 6px; cursor: pointer; font-weight: 600; font-size: 0.825rem; }
.btn-cancel { background: #f1f5f9; color: #475569; border: 1px solid #cbd5e1; }
.btn-save { background: #2563eb; color: white; border: none; }
.btn-danger-sub { background-color: #dc2626 !important; }

@media (max-width: 1100px) {
  .setup-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .preview-card { grid-column: 1 / -1; max-height: none; }
}

@media (max-width: 767px) {
  .setup-grid { grid-template-columns: 1fr; }
  .app-content-wrapper { grid-template-columns: 1fr; gap: 14px; }
  .courts-section { max-height: none; overflow-y: visible; }
  .queue-section { position: static; max-height: 450px; }
  .courts-grid { grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); }
}
</style>
