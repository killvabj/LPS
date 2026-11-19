<script setup lang="ts">
/**
 * APS V1 4号位 — 个人中心
 *
 * 2026-11-18：整块换成后端真实数据（此前是**假数据**：写死的 admin / 18888888888 / 502431556@qq.com）
 *   —— 依据 3号位《个人中心改资料与改密码端点落码_回执 v1.0_20261117》Q3 / §四。
 *   - 左侧读 `GET /api/auth/me`（`userCode` / `userName` / `roles`），角色码翻中文（`roleLabelOf`）。
 *   - Q3：**撤头像入口**（本轮后端不出头像端点，不留假按钮）⇒ 头像只作静态展示，不可点。
 *   - §四-1：`/me` **仍不含** Email / PhoneNumber ⇒ 左侧这两项在页加载时显示「未提供」，
 *     改完资料后用 R1 回包里的值刷新（刷新页面后又回到「未提供」——这是后端当前的能力边界，不是假数据）。
 *   - §四-2：改完资料姓名来自 JWT claim ⇒ 已在 EditInfo 里重签 token，本页姓名按回包值立即刷新。
 */

import { ContentWrap } from '@/components/ContentWrap'
import { onMounted, reactive, ref } from 'vue'
import { ElAlert, ElDivider, ElImage, ElTag, ElTabPane, ElTabs } from 'element-plus'
import defaultAvatar from '@/assets/imgs/avatar.jpg'
import EditInfo from './components/EditInfo.vue'
import EditPassword from './components/EditPassword.vue'
import { roleLabelOf, type RoleKey, type UpdateProfileResult } from '@/api/aps-v1'
import { useApsAuthStore } from '@/store/modules/aps/auth'

const apsAuth = useApsAuthStore()

/** 左侧展示的资料（只放后端真值；没拿到的显示「未提供」） */
const profile = reactive({
  userCode: '',
  userName: '',
  email: '',
  phoneNumber: '',
  roles: [] as RoleKey[]
})

const loadError = ref('')

async function loadProfile(): Promise<void> {
  loadError.value = ''
  await apsAuth.loadUserInfo()
  const info = apsAuth.userInfo
  if (!info) {
    loadError.value = apsAuth.error || '没取到当前用户信息，请稍后刷新重试'
    return
  }
  profile.userCode = info.userCode
  profile.userName = info.userName
  profile.roles = info.roles ?? []
  // /me 不返回邮箱/手机号（3号位 §四-1）⇒ 页加载时留空，显示「未提供」
}

const activeName = ref('first')

onMounted(() => {
  void loadProfile()
})

/** 改资料成功：左栏用后端回包的三项刷新（邮箱/手机号只有这里才有真值） */
function onProfileUpdated(result: UpdateProfileResult): void {
  profile.userName = result.userName || profile.userName
  profile.email = result.email ?? ''
  profile.phoneNumber = result.phoneNumber ?? ''
}
</script>

<template>
  <div class="flex w-100% h-100%">
    <ContentWrap title="个人信息" class="w-400px">
      <ElAlert
        v-if="loadError"
        type="error"
        :closable="false"
        show-icon
        title="加载失败"
        class="mb-12px"
      >
        {{ loadError }}
      </ElAlert>

      <div class="flex justify-center items-center">
        <div class="avatar w-[150px] h-[150px]">
          <ElImage class="w-[150px] h-[150px] rounded-full" :src="defaultAvatar" fit="fill" />
        </div>
      </div>
      <ElDivider />
      <div class="flex justify-between items-center">
        <div>账号：</div>
        <div>{{ profile.userCode || '未提供' }}</div>
      </div>
      <ElDivider />
      <div class="flex justify-between items-center">
        <div>姓名：</div>
        <div>{{ profile.userName || '未提供' }}</div>
      </div>
      <ElDivider />
      <div class="flex justify-between items-center">
        <div>手机号码：</div>
        <div>{{ profile.phoneNumber || '未提供' }}</div>
      </div>
      <ElDivider />
      <div class="flex justify-between items-center">
        <div>用户邮箱：</div>
        <div>{{ profile.email || '未提供' }}</div>
      </div>
      <ElDivider />
      <div class="flex justify-between items-center">
        <div>所属角色：</div>
        <div>
          <template v-if="profile.roles.length">
            <ElTag
              v-for="item in profile.roles"
              :key="item"
              class="ml-2 mb-w"
              type="primary"
              effect="plain"
            >
              {{ roleLabelOf(item) }}
            </ElTag>
          </template>
          <template v-else>未提供</template>
        </div>
      </div>
      <ElDivider />
    </ContentWrap>
    <ContentWrap title="基本资料" class="flex-[3] ml-20px">
      <ElTabs v-model="activeName">
        <ElTabPane label="基本信息" name="first">
          <EditInfo :profile="profile" @updated="onProfileUpdated" />
        </ElTabPane>
        <ElTabPane label="修改密码" name="second">
          <EditPassword />
        </ElTabPane>
      </ElTabs>
    </ContentWrap>
  </div>
</template>

<style lang="less" scoped>
.avatar {
  position: relative;
}
</style>
