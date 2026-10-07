<script setup lang="ts">
  import { ref, computed, onMounted, onUnmounted } from 'vue';
  import { VueDraggableNext as draggable } from 'vue-draggable-next';
  import axios from 'axios';

  // --- 1. 資料型別定義 ---

  export interface Player {
    id: number | string;
    name: string;
    gender: string;
    playedCount: number;
    level: string; // 1 ~ 15 級
    status?: '1' | '0'; // '1': 排隊中/比賽中, '0': 休息/請假
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

  // --- 2. 判斷環境與狀態宣告 ---

  // 自動判斷目前是開發環境 (npm run dev) 還是正式上線環境 (npm run build)
  const isDev = import.meta.env.DEV; 
  const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'https://badminton-api-bqbs.onrender.com/api/players';
  const STORAGE_KEY = 'badminton_queue_players_data';

  const queuePlayers = ref<Player[]>([]);
  const isLoading = ref<boolean>(false);

  // Modal 狀態
  const isEditModalOpen = ref<boolean>(false);
  const editingPlayer = ref<Player | null>(null);

  const isAddModalOpen = ref<boolean>(false);
  const newPlayer = ref<Omit<Player, 'id'>>({
    name: '',
    gender: 'M',
    playedCount: 0,
    level: '5',
    status: '1'
  });

  // 批次選擇狀態
  const isBatchMode = ref<boolean>(false);
  const selectedPlayerIds = ref<(number | string)[]>([]);

  // 場地數量與清單
  const courtNumbersOptions = [1, 2, 3, 4, 5, 6, 7, 8];
  const selectedCourtCount = ref<number>(5);

  // 控制佇列分區收合 (預設展開: false)
  const collapsedGroups = ref<Record<string, boolean>>({
    high: false,
    mid: false,
    low: false,
    fresh: false,
    moderate: false,
    veteran: false,
    restingGroup: false
  });

  const courts = ref<Court[]>(
    Array.from({ length: 5 }, (_, i) => ({
      id: i + 1,
      name: `${i + 1}號場`,
      players: [],
      nextPlayers: [],
      status: 'idle',
      startTime: null,
      elapsedSeconds: 0
    }))
  );

  // --- 3. localStorage 輔助函式 (僅在生產環境或 API 失敗時使用) ---

  const saveToLocalStorage = () => {
    if (!isDev) {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(queuePlayers.value));
    }
  };

  const loadFromLocalStorage = (): Player[] => {
    const data = localStorage.getItem(STORAGE_KEY);
    if (data) {
      try {
        return JSON.parse(data);
      } catch (e) {
        console.error('解析 localStorage 失敗:', e);
      }
    }
    // 預設初始靜態資料
    return [
      { id: 1, name: '戴資穎', gender: 'F', playedCount: 0, level: '15', status: '1' },
      { id: 2, name: '林丹', gender: 'M', playedCount: 0, level: '15', status: '1' },
      { id: 3, name: '周天成', gender: 'M', playedCount: 0, level: '11', status: '1' },
      { id: 4, name: '山口茜', gender: 'F', playedCount: 0, level: '14', status: '1' },
      { id: 5, name: '李洋', gender: 'M', playedCount: 0, level: '12', status: '1' },
      { id: 6, name: '王齊麟', gender: 'M', playedCount: 0, level: '12', status: '1' },
      { id: 7, name: '球友A', gender: 'M', playedCount: 1, level: '8', status: '1' },
      { id: 8, name: '球友B', gender: 'F', playedCount: 0, level: '4', status: '0' }
    ];
  };

  // --- 4. 等待佇列排序與分區計算 ---

  const queueGroupMode = ref<'level' | 'playedCount' | 'resting'>('level');

  const toggleGroupMode = (mode: 'level' | 'playedCount' | 'resting') => {
    queueGroupMode.value = mode;
    selectedPlayerIds.value = [];
  };

  const currentTabPlayers = computed(() => {
    if (queueGroupMode.value === 'resting') {
      return queuePlayers.value.filter(p => String(p.status) === '0');
    } else {
      return queuePlayers.value.filter(p => String(p.status) !== '0');
    }
  });

  const sortedQueuePlayers = computed(() => {
    return [...queuePlayers.value].sort((a, b) => {
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
      return [
        { title: '💤 休息請假中', key: 'restingGroup', players: restingPlayers }
      ];
    }

    const activePlayers = sortedQueuePlayers.value.filter(p => String(p.status) !== '0');

    if (queueGroupMode.value === 'level') {
      const high: Player[] = [];
      const mid: Player[] = [];
      const low: Player[] = [];

      activePlayers.forEach(p => {
        const lvl = Number(p.level) || 0;
        if (lvl >= 11) high.push(p);
        else if (lvl >= 6) mid.push(p);
        else low.push(p);
      });

      return [
        { title: '🔥 高階分區 (11~15級)', key: 'high', players: high },
        { title: '⚡ 中階分區 (6~10級)', key: 'mid', players: mid },
        { title: '🌱 初階分區 (1~5級)', key: 'low', players: low }
      ];
    } else {
      const fresh: Player[] = [];
      const moderate: Player[] = [];
      const veteran: Player[] = [];

      activePlayers.forEach(p => {
        const count = p.playedCount || 0;
        if (count === 0) fresh.push(p);
        else if (count <= 2) moderate.push(p);
        else veteran.push(p);
      });

      return [
        { title: '🆕 未上場 (0場)', key: 'fresh', players: fresh },
        { title: '🎾 已上場一半 (1~2場)', key: 'moderate', players: moderate },
        { title: '🔥 好多場 (3場以上)', key: 'veteran', players: veteran }
      ];
    }
  });

  const toggleGroupCollapse = (key: string) => {
    collapsedGroups.value[key] = !collapsedGroups.value[key];
  };

  // --- 5. 初始化與 API / 本地資料載入 ---

  const fetchPlayersFromAPI = async () => {
    // 情況 A：正式發布環境 (Production) -> 純前端 localStorage 模式
    if (!isDev) {
      queuePlayers.value = loadFromLocalStorage();
      return;
    }

    // 情況 B：本地開發環境 (Development) -> 發送 HTTP API 請求
    try {
      isLoading.value = true;
      const response = await axios.get<Player[]>(API_BASE_URL);
      queuePlayers.value = response.data;
    } catch (error) {
      console.error('API 連線失敗，載入預設資料:', error);
      queuePlayers.value = loadFromLocalStorage();
    } finally {
      isLoading.value = false;
    }
  };

  // --- 6. 計時器邏輯 ---

  const timerInterval = ref<ReturnType<typeof setInterval> | null>(null);

  const startGlobalTimer = () => {
    if (timerInterval.value) return;
    timerInterval.value = setInterval(() => {
      const now = Date.now();
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
    fetchPlayersFromAPI();
    startGlobalTimer();
  });

  onUnmounted(() => {
    if (timerInterval.value) clearInterval(timerInterval.value);
  });

  // --- 7. 拖曳連動處理 (Drag & Drop Sync) ---

  const getDraggedPlayer = (evt: any): Player | null => {
    return evt?.item?._underlying_vm_ || evt?.draggedContext?.element || null;
  };

  const handlePlayerAddedToCourt = (evt: any) => {
    const player = getDraggedPlayer(evt);
    if (!player) return;

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
      queuePlayers.value.push(player);
      saveToLocalStorage();
    }
  };

  // --- 8. 選手 CRUD 與動態批次操作 ---

  const openAddModal = () => {
    newPlayer.value = { name: '', gender: 'M', playedCount: 0, level: '5', status: '1' };
    isAddModalOpen.value = true;
  };

  const closeAddModal = () => { isAddModalOpen.value = false; };

  const saveNewPlayer = async () => {
    if (!newPlayer.value.name.trim()) return alert('請輸入選手姓名！');

    const localNewPlayer: Player = {
      id: Date.now(),
      name: newPlayer.value.name.trim(),
      gender: newPlayer.value.gender,
      playedCount: Number(newPlayer.value.playedCount || 0),
      level: String(newPlayer.value.level),
      status: newPlayer.value.status || '1'
    };

    if (isDev) {
      const payload = {
        id: "",
        name: localNewPlayer.name,
        gender: localNewPlayer.gender,
        playedCount: localNewPlayer.playedCount,
        level: localNewPlayer.level,
        status: localNewPlayer.status
      };
      try {
        const response = await axios.post<Player>(API_BASE_URL, payload);
        queuePlayers.value.push(response.data);
      } catch (err) {
        queuePlayers.value.push(localNewPlayer);
      }
    } else {
      queuePlayers.value.push(localNewPlayer);
      saveToLocalStorage();
    }
    closeAddModal();
  };

  const openEditModal = (player: Player) => {
    editingPlayer.value = JSON.parse(JSON.stringify(player));
    isEditModalOpen.value = true;
  };

  const closeEditModal = () => {
    isEditModalOpen.value = false;
    editingPlayer.value = null;
  };

  const savePlayerEdit = async () => {
    if (!editingPlayer.value) return;
    const targetId = editingPlayer.value.id;

    if (isDev) {
      const payload = {
        id: String(targetId),
        name: editingPlayer.value.name.trim(),
        gender: editingPlayer.value.gender,
        playedCount: Number(editingPlayer.value.playedCount),
        level: String(editingPlayer.value.level),
        status: editingPlayer.value.status
      };

      try {
        await axios.put(`${API_BASE_URL}/${targetId}`, payload);
        queuePlayers.value = queuePlayers.value.map(p => 
          String(p.id) === String(targetId) ? { ...editingPlayer.value! } : p
        );
      } catch (err) {
        alert('更新失敗，請檢查網路連線！');
        return;
      }
    } else {
      queuePlayers.value = queuePlayers.value.map(p => 
        String(p.id) === String(targetId) ? { ...editingPlayer.value! } : p
      );
      saveToLocalStorage();
    }
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

  const updateBatchStatus = async (targetStatus: '0' | '1') => {
    if (selectedPlayerIds.value.length === 0) return;
    const targetPlayers = queuePlayers.value.filter(p => selectedPlayerIds.value.includes(p.id));

    targetPlayers.forEach(p => p.status = targetStatus);

    if (isDev) {
      await Promise.all(targetPlayers.map(async (player) => {
        try {
          await axios.put(`${API_BASE_URL}/${player.id}`, {
            id: String(player.id),
            name: player.name,
            gender: player.gender,
            playedCount: Number(player.playedCount),
            level: String(player.level),
            status: targetStatus
          });
        } catch (err) { console.warn(err); }
      }));
    } else {
      saveToLocalStorage();
    }

    selectedPlayerIds.value = [];
    isBatchMode.value = false;
  };

  const batchResetPlayedCount = async () => {
    if (selectedPlayerIds.value.length === 0) return;
    if (!confirm(`確定要將選中的 ${selectedPlayerIds.value.length} 位選手已打場數歸零嗎？`)) return;

    isLoading.value = true;
    const targetPlayers = queuePlayers.value.filter(p => selectedPlayerIds.value.includes(p.id));

    targetPlayers.forEach(p => p.playedCount = 0);

    if (isDev) {
      await Promise.all(targetPlayers.map(async (player) => {
        try {
          await axios.put(`${API_BASE_URL}/${player.id}`, {
            id: String(player.id),
            name: player.name,
            gender: player.gender,
            playedCount: 0,
            level: String(player.level),
            status: player.status || '1'
          });
        } catch (err) { console.warn(err); }
      }));
    } else {
      saveToLocalStorage();
    }

    isLoading.value = false;
    selectedPlayerIds.value = [];
    isBatchMode.value = false;
  };

  const batchDeletePlayers = async () => {
    if (selectedPlayerIds.value.length === 0) return;
    if (!confirm(`確定要刪除選中的 ${selectedPlayerIds.value.length} 位選手嗎？`)) return;

    const idsToDelete = [...selectedPlayerIds.value];

    if (isDev) {
      await Promise.all(idsToDelete.map(async id => {
        try { await axios.delete(`${API_BASE_URL}/${id}`); }
        catch (err) { console.warn(err); }
      }));
    }

    queuePlayers.value = queuePlayers.value.filter(p => !idsToDelete.includes(p.id));
    saveToLocalStorage();

    selectedPlayerIds.value = [];
    isBatchMode.value = false;
  };

  // --- 9. 自動配對與場地管理 ---

  const isPreferredLevel = (courtPlayers: Player[], targetPlayer: Player): boolean => {
    if (courtPlayers.length === 0) return true;
    const targetLevel = Number(targetPlayer.level);
    if (isNaN(targetLevel)) return true;
    return courtPlayers.every(p => Math.abs(Number(p.level) - targetLevel) <= 2);
  };

  const fillCourtAuto = (court: Court) => {
    const activeQueue = queuePlayers.value.filter(p => String(p.status) !== '0');
    activeQueue.sort((a, b) => a.playedCount - b.playedCount);

    const fillZone = (targetZone: 'players' | 'nextPlayers', strictLevel: boolean) => {
      let i = 0;
      while (court[targetZone].length < 4 && i < activeQueue.length) {
        const candidate = activeQueue[i];
        if (candidate && (!strictLevel || isPreferredLevel(court[targetZone], candidate))) {
          court[targetZone].push(candidate);
          const idx = queuePlayers.value.findIndex(p => String(p.id) === String(candidate.id));
          if (idx !== -1) queuePlayers.value.splice(idx, 1);
          activeQueue.splice(i, 1);
        } else { i++; }
      }
    };

    if (court.status === 'idle' && court.players.length < 4) {
      fillZone('players', true);
      fillZone('players', false);
    }
    if (court.nextPlayers.length < 4) {
      fillZone('nextPlayers', true);
      fillZone('nextPlayers', false);
    }

    saveToLocalStorage();
  };

  const autoAssignAllCourts = () => {
    if (queuePlayers.value.length === 0) return alert('目前佇列中沒有球員！');
    courts.value.forEach(court => fillCourtAuto(court));
  };

  const updateCourtCount = () => {
    const currentCount = courts.value.length;
    const newCount = selectedCourtCount.value;

    if (newCount > currentCount) {
      for (let i = currentCount + 1; i <= newCount; i++) {
        courts.value.push({ id: i, name: `${i}號場`, players: [], nextPlayers: [], status: 'idle', startTime: null, elapsedSeconds: 0 });
      }
    } else if (newCount < currentCount) {
      const removedCourts = courts.value.splice(newCount);
      removedCourts.forEach(court => {
        court.players.concat(court.nextPlayers).forEach(p => {
          if (!queuePlayers.value.some(q => String(q.id) === String(p.id))) {
            queuePlayers.value.push(p);
          }
        });
      });
      saveToLocalStorage();
    }
  };

  const addPlayerToAvailableCourt = (player: Player) => {
    const targetCourt = courts.value.find(c => c.status === 'idle' && c.players.length < 4 && isPreferredLevel(c.players, player))
      || courts.value.find(c => c.nextPlayers.length < 4 && isPreferredLevel(c.nextPlayers, player))
      || courts.value.find(c => c.status === 'idle' && c.players.length < 4)
      || courts.value.find(c => c.nextPlayers.length < 4);

    if (!targetCourt) return alert('目前所有場地與預備區皆已額滿！');

    const zone = (targetCourt.status === 'idle' && targetCourt.players.length < 4) ? 'players' : 'nextPlayers';
    const index = queuePlayers.value.findIndex(p => String(p.id) === String(player.id));
    if (index !== -1) {
      queuePlayers.value.splice(index, 1);
      targetCourt[zone].push(player);
      saveToLocalStorage();
    }
  };

  const startMatch = (court: Court) => {
    if (court.players.length !== 4) {
      return alert(`【${court.name}】目前有 ${court.players.length} 人，必須湊齊 4 人才能開始比賽！`);
    }
    court.status = 'playing';
    court.startTime = Date.now();
    court.elapsedSeconds = 0;
  };

  const finishMatch = async (courtId: number) => {
    const court = courts.value.find(c => c.id === courtId);
    if (!court) return;

    isLoading.value = true;
    try {
      court.players.forEach(p => p.playedCount += 1);

      if (isDev) {
        await Promise.all(court.players.map(async (player) => {
          try {
            await axios.put(`${API_BASE_URL}/${player.id}`, {
              id: String(player.id),
              name: player.name,
              gender: player.gender,
              playedCount: player.playedCount,
              level: String(player.level),
              status: player.status || '1'
            });
          } catch (err) { console.warn(err); }
        }));
      }

      court.players.forEach(p => {
        if (!queuePlayers.value.some(existing => String(existing.id) === String(p.id))) {
          queuePlayers.value.push(p);
        }
      });

      court.players = [...court.nextPlayers];
      court.nextPlayers = [];
      court.status = 'idle';
      court.startTime = null;
      court.elapsedSeconds = 0;

      fillCourtAuto(court);
      saveToLocalStorage();
    } finally {
      isLoading.value = false;
    }
  };

  const clearAllCourts = async () => {
    if (courts.value.some(c => c.status === 'playing') && !confirm('有場地正進行比賽，確定要清空嗎？')) return;

    isLoading.value = true;
    const playersToReturn: Player[] = [];

    courts.value.forEach(court => {
      court.players.forEach(p => playersToReturn.push(p));
      court.nextPlayers.forEach(p => playersToReturn.push(p));

      court.players = [];
      court.nextPlayers = [];
      court.status = 'idle';
      court.startTime = null;
      court.elapsedSeconds = 0;
    });

    playersToReturn.forEach(player => {
      if (!queuePlayers.value.some(q => String(q.id) === String(player.id))) {
        queuePlayers.value.push(player);
      }
    });

    if (isDev) {
      try {
        await Promise.all(
          playersToReturn.map(async (player) => {
            await axios.put(`${API_BASE_URL}/${player.id}`, {
              id: String(player.id),
              name: player.name,
              gender: player.gender,
              playedCount: Number(player.playedCount),
              level: String(player.level),
              status: player.status || '1'
            });
          })
        );
      } catch (err) {
        console.error('清空場地時同步後端失敗：', err);
      }
    }

    saveToLocalStorage();
    isLoading.value = false;
  };

  // --- 10. 輔助設定 ---

  const dragOptions = computed(() => ({
    animation: 200,
    group: "badminton-app",
    ghostClass: "ghost",
    delay: 50,
    delayOnTouchOnly: true
  }));

  const getLevelClass = (level: string | number) => {
    const num = Number(level);
    return (!isNaN(num) && num >= 1 && num <= 15) ? `level-${num}` : '';
  };
</script>

<template>
  <div class="app-main-layout">
    <header class="app-header">
      <div class="header-content">
        <h1 class="app-title">
          🏸 羽球場邊排點看板 
          <span class="mode-badge" :class="isDev ? 'dev' : 'prod'">
            {{ isDev ? '⚡ 本地開發 API 模式' : '💾 正式離線暫存模式' }}
          </span>
        </h1>
        <div class="header-controls">
          <button class="btn-add-player" @click="openAddModal">➕ 選手報名</button>
          <button class="btn-auto-assign" @click="autoAssignAllCourts">⚡ 一鍵自動排點</button>
          <button class="btn-clear-courts" @click="clearAllCourts" :disabled="isLoading">🗑 清空場地</button>

          <div class="court-selector">
            <label for="courtCount">場地數量:</label>
            <select id="courtCount" v-model="selectedCourtCount" @change="updateCourtCount">
              <option v-for="num in courtNumbersOptions" :key="num" :value="num">{{ num }} 個場地</option>
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
                <span class="player-count" :class="{ 'full': court.players.length === 4 }">{{ court.players.length }} / 4 人</span>
              </div>
            </div>

            <!-- 現正對局 -->
            <div class="zone-label">現正對局</div>
            <draggable class="player-list court-player-list"
                       v-model="court.players"
                       v-bind="dragOptions"
                       :disabled="court.status === 'playing'"
                       :set-data="false"
                       @add="handlePlayerAddedToCourt">
              <div v-for="player in court.players" :key="player.id" class="player-card touch-action-none">
                <div class="player-info">
                  <span class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ player.gender === 'M' ? '♂' : '♀' }}</span>
                  <span class="player-name">{{ player.name }}</span>
                </div>
                <div class="player-stats">
                  <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                  <span class="played-tag">{{ player.playedCount }}場</span>
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
                  <span class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ player.gender === 'M' ? '♂' : '♀' }}</span>
                  <span class="player-name">{{ player.name }}</span>
                </div>
                <div class="player-stats">
                  <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                  <span class="played-tag">{{ player.playedCount }}場</span>
                </div>
              </div>
            </draggable>

            <!-- 控制按鈕 -->
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
            <h2>⏳ 等待佇列 ({{ queuePlayers.length }} 人)</h2>
            <button class="btn-toggle-batch" :class="{ 'active': isBatchMode }" @click="toggleBatchMode">
              {{ isBatchMode ? '✕ 取消選擇' : '☑ 批次選擇' }}
            </button>
          </div>

          <!-- 分區 Tab -->
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

          <!-- 批次操作工具列 -->
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
          <span v-else class="header-tip">💡 點擊分區標題可展開/收合選手列表</span>
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
                     'is-resting': String(player.status) === '0'
                   }"
                   @click="isBatchMode ? toggleSelectPlayer(player) : openEditModal(player)">

                <div v-if="isBatchMode" class="checkbox-wrapper">
                  <input type="checkbox" 
                         :checked="selectedPlayerIds.includes(player.id)" 
                         @click.stop="toggleSelectPlayer(player)" />
                </div>

                <div class="player-info">
                  <span class="player-gender" :class="player.gender === 'M' ? 'male' : 'female'">{{ player.gender === 'M' ? '♂' : '♀' }}</span>
                  <span class="player-name">{{ player.name }}</span>
                  <span v-if="String(player.status) === '0'" class="resting-badge">💤 休息</span>
                </div>

                <div class="player-actions-right">
                  <div class="player-stats">
                    <span class="level-tag" :class="getLevelClass(player.level)">{{ player.level }}級</span>
                    <span class="played-tag">已打: {{ player.playedCount }}場</span>
                  </div>
                  <button v-if="!isBatchMode && String(player.status) !== '0'" class="btn-quick-add" title="快速排入" @click.stop="addPlayerToAvailableCourt(player)">➕</button>
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

    <!-- Modal: 編輯選手 -->
    <div v-if="isEditModalOpen && editingPlayer" class="modal-overlay" @click.self="closeEditModal">
      <div class="modal-card">
        <div class="modal-header">
          <h3>✏️ 編輯選手資料</h3>
          <button class="btn-close" @click="closeEditModal">✕</button>
        </div>
        <div class="modal-body">
          <div class="form-group">
            <label>姓名：</label>
            <input type="text" v-model="editingPlayer.name" class="form-input" />
          </div>
          <div class="form-group">
            <label>性別：</label>
            <select v-model="editingPlayer.gender" class="form-select">
              <option value="M">♂ 男 (Male)</option>
              <option value="F">♀ 女 (Female)</option>
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
  </div>
</template>

<style scoped>
.mode-badge {
  font-size: 0.75rem;
  padding: 2px 8px;
  border-radius: 12px;
  margin-left: 8px;
  vertical-align: middle;
}
.mode-badge.dev {
  background-color: #fff3cd;
  color: #856404;
  border: 1px solid #ffeeba;
}
.mode-badge.prod {
  background-color: #d4edda;
  color: #155724;
  border: 1px solid #c3e6cb;
}
</style>

<style scoped>
  .app-main-layout {
    height: 100dvh;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    background-color: #f0f2f5;
    color: #333;
  }

  .app-header {
    height: 60px;
    background: white;
    padding: 0 20px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.06);
    display: flex;
    align-items: center;
    z-index: 10;
  }

  .header-content {
    width: 100%;
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

  .header-controls {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .app-title {
    font-size: 1.2rem;
    font-weight: 700;
    margin: 0;
    color: #2c3e50;
  }

  .btn-add-player, .btn-auto-assign, .btn-clear-courts {
    border: none;
    padding: 6px 14px;
    border-radius: 6px;
    font-size: 0.88rem;
    font-weight: 600;
    cursor: pointer;
    transition: all 0.2s ease;
    color: white;
  }

  .btn-add-player {
    background: #52c41a;
    box-shadow: 0 2px 6px rgba(82, 196, 26, 0.3);
  }

    .btn-add-player:hover {
      background-color: #73d13d;
      transform: translateY(-1px);
    }

  .btn-auto-assign {
    background: linear-gradient(135deg, #722ed1, #1890ff);
    box-shadow: 0 2px 6px rgba(114, 46, 209, 0.3);
  }

    .btn-auto-assign:hover {
      opacity: 0.9;
      transform: translateY(-1px);
    }

  .btn-clear-courts {
    background: #ff4d4f;
    box-shadow: 0 2px 6px rgba(255, 77, 79, 0.3);
  }

    .btn-clear-courts:hover {
      background-color: #ff7875;
      transform: translateY(-1px);
    }

  .court-selector {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 0.9rem;
  }

    .court-selector select {
      padding: 4px 8px;
      border-radius: 4px;
      border: 1px solid #ccc;
    }

  .app-content-wrapper {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    padding: 10px;
    gap: 10px;
  }

  .courts-section, .queue-section {
    background: white;
    border-radius: 10px;
    box-shadow: 0 2px 6px rgba(0,0,0,0.04);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .courts-grid, .queue-container {
    flex: 1;
    overflow-y: auto;
    padding: 10px;
    -webkit-overflow-scrolling: touch;
  }

  .queue-container {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .queue-group {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .group-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    font-size: 0.85rem;
    font-weight: bold;
    color: #1d39c4;
    background-color: #f0f5ff;
    padding: 6px 10px;
    border-radius: 6px;
    border-left: 4px solid #2f54eb;
    cursor: pointer;
    user-select: none;
    transition: background-color 0.2s;
  }

    .group-header:hover {
      background-color: #d6e4ff;
    }

  .group-title {
    display: flex;
    align-items: center;
    gap: 6px;
  }

  .collapse-icon {
    font-size: 0.7rem;
    color: #2f54eb;
  }

  .group-count {
    font-size: 0.75rem;
    color: #8c8c8c;
    background: rgba(255, 255, 255, 0.8);
    padding: 1px 6px;
    border-radius: 10px;
  }

  @media (min-width: 769px) {
    .app-content-wrapper {
      flex-direction: row;
    }

    .courts-section {
      flex: 3;
    }

    .queue-section {
      flex: 1;
      min-width: 320px;
    }
  }

  .section-header {
    padding: 10px 15px;
    border-bottom: 1px solid #eee;
    background-color: #fafafa;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

    .section-header h2 {
      font-size: 1rem;
      margin: 0;
      font-weight: 600;
      color: #555;
    }

  .header-tip {
    font-size: 0.75rem;
    color: #888;
  }

  .header-title-group {
    display: flex;
    justify-content: space-between;
    align-items: center;
    width: 100%;
  }

  .btn-toggle-batch {
    background: #ffffff;
    border: 1px solid #d9d9d9;
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.8rem;
    font-weight: 600;
    color: #595959;
  }

    .btn-toggle-batch.active {
      background: #1890ff;
      color: white;
      border-color: #1890ff;
    }

  .batch-actions-bar {
    display: flex;
    align-items: center;
    gap: 8px;
    background: #e6f7ff;
    padding: 6px 10px;
    border-radius: 6px;
    border: 1px solid #91d5ff;
  }

  .btn-batch-select-all {
    background: white;
    border: 1px solid #91d5ff;
    color: #1890ff;
    padding: 2px 8px;
    border-radius: 4px;
    font-size: 0.75rem;
    cursor: pointer;
  }

  .selected-count {
    font-size: 0.8rem;
    color: #1890ff;
    font-weight: bold;
  }

  .batch-btns {
    margin-left: auto;
    display: flex;
    gap: 6px;
  }

  .btn-batch-rest, .btn-batch-active, .btn-batch-delete {
    color: white;
    border: none;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 0.75rem;
    font-weight: bold;
    cursor: pointer;
  }

  .btn-batch-rest {
    background: #fa8c16;
  }

  .btn-batch-active {
    background: #52c41a;
  }

  .btn-batch-delete {
    background: #ff4d4f;
  }

    .btn-batch-rest:disabled, .btn-batch-active:disabled, .btn-batch-delete:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }

  .courts-grid {
    flex: 1;
    display: flex;
    flex-wrap: wrap;
    align-content: stretch; /* 讓多行時高度自動拉伸 */
    gap: 10px;
    padding: 10px;
    overflow-y: auto;
    -webkit-overflow-scrolling: touch;
  }

  .court-card {
    flex: 1 1 calc(33.333% - 10px); /* 預設一排約 3 個，可依剩餘空間伸縮 */
    min-width: 260px;
    background: #fdfdfd;
    border: 2px solid #e0e0e0;
    border-radius: 8px;
    padding: 10px;
    box-sizing: border-box;
    /* 垂直彈性佈局，內部元件均勻分佈 */
    display: flex;
    flex-direction: column;
    justify-content: space-between;
  }

    .court-card.playing {
      border-color: #52c41a;
      background-color: #f6ffed;
    }

  /* 少量場地時自動放大填滿 (僅 1 個場地時佔滿 100%) */
  .courts-grid:has(.court-card:nth-last-child(1):first-child) .court-card {
    flex: 1 1 100%;
  }

  /* 僅 2 個場地時各佔 50% */
  .courts-grid:has(.court-card:nth-last-child(2):first-child) .court-card,
  .courts-grid:has(.court-card:nth-last-child(2):first-child) ~ .court-card {
    flex: 1 1 calc(50% - 10px);
  }

  .court-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 8px;
    padding-bottom: 5px;
    border-bottom: 1px dashed #ddd;
  }

  .court-header-right, .title-with-status {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .court-header h3 {
    margin: 0;
    font-size: 1rem;
  }

  .status-badge {
    font-size: 0.7rem;
    padding: 2px 6px;
    border-radius: 4px;
    background: #eee;
    color: #666;
  }

    .status-badge.playing {
      background: #52c41a;
      color: white;
    }

  .match-timer {
    font-size: 0.8rem;
    font-weight: bold;
    color: #cf1322;
    background: #fff1f0;
    padding: 2px 6px;
    border-radius: 4px;
    border: 1px solid #ffa39e;
  }

  .player-count {
    font-size: 0.8rem;
    color: #666;
    background: #eee;
    padding: 2px 6px;
    border-radius: 10px;
  }

    .player-count.full {
      background-color: #1890ff;
      color: white;
    }

  .zone-label {
    font-size: 0.75rem;
    font-weight: bold;
    color: #1890ff;
    margin: 6px 0 2px 2px;
  }

  .next-zone-label {
    color: #fa8c16;
    margin-top: 10px;
  }

  .court-player-list {
    min-height: 80px;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .court-actions {
    margin-top: 10px;
  }

  .btn-start-match, .btn-finish-match {
    width: 100%;
    padding: 8px;
    color: white;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.9rem;
    font-weight: 600;
  }

  .btn-start-match {
    background-color: #1890ff;
  }

  .btn-finish-match {
    background-color: #ff4d4f;
  }

  .queue-player-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-height: 45px;
  }

  .player-list {
    background-color: #fafafa;
    border: 2px dashed #ddd;
    border-radius: 6px;
    padding: 5px;
  }

  .player-card {
    padding: 8px 12px;
    background: white;
    border: 1px solid #ddd;
    border-left: 4px solid #1890ff;
    border-radius: 4px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 8px;
    cursor: grab;
    user-select: none;
    touch-action: none;
  }

    .player-card.clickable:hover {
      background-color: #e6f7ff;
      border-color: #91d5ff;
    }

    .player-card.is-selected {
      border: 2px solid #1890ff !important;
      background-color: #e6f7ff !important;
    }

    .player-card.is-resting {
      background-color: #f5f5f5;
      opacity: 0.6;
      border-left-color: #d9d9d9;
    }

  .checkbox-wrapper {
    margin-right: 8px;
  }

  .ghost {
    opacity: 0.4;
    background: #c8ebfb;
  }

  .player-info {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: 1;
    min-width: 0; /* 允許長文字區域正常防護縮放 */
  }

  .player-stats, .player-actions-right {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0; /* 防止右側標籤與按鈕被壓縮 */
  }

  .player-name {
    max-width: 100px;
    word-break: break-all;
    white-space: normal;
    line-height: 1.25;
  }

  .player-gender {
    font-size: 0.8rem;
    font-weight: bold;
    flex-shrink: 0;
  }

  .male {
    color: #1890ff;
  }

  .female {
    color: #f5222d;
  }

  .played-tag {
    font-size: 0.75rem;
    color: #888;
    background: #eee;
    padding: 1px 5px;
    border-radius: 4px;
  }

  .resting-badge {
    font-size: 0.7rem;
    background-color: #ff4d4f;
    color: white;
    padding: 1px 5px;
    border-radius: 4px;
  }

  .btn-quick-add {
    background-color: #52c41a;
    color: white;
    border: none;
    border-radius: 50%;
    width: 26px;
    height: 26px;
    font-size: 0.85rem;
    cursor: pointer;
  }

  .level-tag {
    font-size: 0.75rem;
    padding: 1px 6px;
    border-radius: 4px;
    color: white;
    font-weight: bold;
  }

  .level-1, .level-2, .level-3, .level-4, .level-5 {
    background-color: #52c41a;
  }

  .level-6, .level-7, .level-8, .level-9, .level-10 {
    background-color: #fa8c16;
  }

  .level-11, .level-12, .level-13, .level-14, .level-15 {
    background-color: #f5222d;
  }

  .modal-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    background-color: rgba(0, 0, 0, 0.45);
    display: flex;
    justify-content: center;
    align-items: center;
    z-index: 1000;
  }

  .modal-card {
    background: white;
    width: 90%;
    max-width: 380px;
    border-radius: 12px;
    overflow: hidden;
  }

  .modal-header {
    padding: 12px 16px;
    background-color: #fafafa;
    border-bottom: 1px solid #eee;
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

    .modal-header h3 {
      margin: 0;
      font-size: 1.05rem;
    }

  .btn-close {
    background: none;
    border: none;
    font-size: 1.1rem;
    cursor: pointer;
    color: #999;
  }

  .modal-body {
    padding: 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .form-group {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

    .form-group label {
      font-size: 0.85rem;
      font-weight: 600;
      color: #666;
    }

  .form-input, .form-select {
    padding: 8px;
    border: 1px solid #ccc;
    border-radius: 6px;
    font-size: 0.95rem;
  }

  .modal-footer {
    padding: 12px 16px;
    border-top: 1px solid #eee;
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    background-color: #fafafa;
  }

  .btn-cancel {
    padding: 6px 12px;
    background-color: #f5f5f5;
    border: 1px solid #d9d9d9;
    border-radius: 4px;
    cursor: pointer;
  }

  .btn-save {
    padding: 6px 14px;
    background-color: #1890ff;
    color: white;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    font-weight: 600;
  }

  .group-mode-switch {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-top: 4px;
  }

  .switch-label {
    font-size: 0.75rem;
    color: #666;
  }

  .btn-mode-tab {
    background: #f0f0f0;
    border: 1px solid #d9d9d9;
    padding: 2px 8px;
    border-radius: 4px;
    font-size: 0.75rem;
    cursor: pointer;
    color: #595959;
    transition: all 0.2s;
  }

    .btn-mode-tab.active {
      background: #722ed1;
      color: white;
      border-color: #722ed1;
      font-weight: bold;
    }
</style>
