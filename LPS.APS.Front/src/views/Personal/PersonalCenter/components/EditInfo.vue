<script lang="ts" setup>
/**
 * APS V1 4号位 — 个人中心「基本信息」编辑（改自己资料）
 *
 * 2026-11-18：接后端真实端点 `PUT /api/auth/profile`
 *   —— 依据 3号位《个人中心改资料与改密码端点落码_回执 v1.0_20261117》R1 / Q2。
 *   - 只能改三项：姓名 / 邮箱 / 手机号
 *     （登录名、状态、角色、业务范围归管理面 `auth.manage`，不可自助改）。
 *   - 后端语义（`AuthService.UpdateProfileAsync`）：
 *       · `userName` 空白 ⇒ **保持原值**（不是清空！），trim 后 ≤50 位，超了 400；
 *       · `email` / `phoneNumber` 空白 ⇒ **清空（存 null）**，不做格式校验。
 *     ⇒ 本表单把「姓名」设为必填（留空等于不改，没必要提交）；
 *       邮箱/手机号留空 = 真清空，所以提交体必须带空串（不能用会过滤空值的取值方式）。
 *   - Q2：姓名允许重名（显示名不是登录凭据），前端不做重名校验。
 *
 * ⚠️ 旧实现是「假成功」（改完只弹一句「修改成功」，一个字节都没落库）——本轮整块换成真实调用。
 *
 * 2026-11-18（第二封）：`/me` 补 `email`/`phoneNumber`、显示名改读 DB
 *   —— 依据 3号位《个人中心me补邮箱手机号与显示名读库_回执 v1.0_20261118》R1。
 *   本组件**行为不变**：表单三项照旧提交；左栏的值改由父级读 `/me` 真值刷新。
 *   改完资料后的 token 重签**保留为保险**（`/me` 已读 DB ⇒ 姓名不再依赖它）。
 */

import { Form, FormSchema } from '@/components/Form'
import { useForm } from '@/hooks/web/useForm'
import { reactive, ref, watch } from 'vue'
import { useValidator } from '@/hooks/web/useValidator'
import { ElMessage, ElMessageBox, ElDivider } from 'element-plus'
import { apsAuthApi, APS_USE_MOCK, type UpdateProfileResult } from '@/api/aps-v1'
import { useUserStore } from '@/store/modules/user'

const props = defineProps<{
  /** 当前用户资料（来自 /me 或改资料回包） */
  profile: {
    userName: string
    email: string
    phoneNumber: string
  }
}>()

const emit = defineEmits<{
  /** 改资料成功：把后端回包的三项交回父级刷新左栏 */
  updated: [result: UpdateProfileResult]
}>()

const { required, phone, maxlength, email } = useValidator()

const userStore = useUserStore()

const formSchema = reactive<FormSchema[]>([
  {
    field: 'userName',
    label: '姓名',
    component: 'Input',
    colProps: {
      span: 24
    }
  },
  {
    field: 'phoneNumber',
    label: '手机号码',
    component: 'Input',
    colProps: {
      span: 24
    }
  },
  {
    field: 'email',
    label: '邮箱',
    component: 'Input',
    colProps: {
      span: 24
    }
  }
])

const rules = reactive({
  userName: [required(), maxlength(50)],
  phoneNumber: [phone()],
  email: [email()]
})

const { formRegister, formMethods } = useForm()
const { setValues, getFormData, getElFormExpose } = formMethods

watch(
  () => props.profile,
  (value) => {
    setValues({
      userName: value?.userName ?? '',
      phoneNumber: value?.phoneNumber ?? '',
      email: value?.email ?? ''
    })
  },
  {
    immediate: true,
    deep: true
  }
)

const saveLoading = ref(false)

/** 只把中文错误原文摆到页面上（后端 message 已是大白话；axios 英文原文不外泄） */
function plainError(error: unknown, fallback: string): string {
  const msg = (error as Error)?.message ?? ''
  return /[一-龥]/.test(msg) ? msg : fallback
}

/**
 * 改完资料后重签一次 token（**保留为保险**，不再是"姓名生效"的必要条件）
 *
 * ⚠️ 2026-11-18（3号位《个人中心me补邮箱手机号与显示名读库_回执 v1.0_20261118》R1）：
 *   `/me` 的「姓名」已改读 **DB**（`[User].DisplayName`）⇒ 改完资料**立即生效**，
 *   不再等 claim 重签 ⇒ 重签**非必需**。
 *   仍保留的理由：claim 在签发时固化，其它消费 claim 的调用（操作人名等）
 *   要等重签才拿到新名；3号位明示「保留即可、仍无害」。
 *   失败不阻断保存（资料已经落库了）。
 */
async function refreshTokenForNewName(): Promise<void> {
  if (APS_USE_MOCK) return
  const refreshToken = userStore.getRefreshToken
  if (!refreshToken) return
  try {
    const res = await apsAuthApi.refresh({ accessToken: userStore.getToken, refreshToken })
    userStore.setAuthBundle(res.accessToken, res.refreshToken, res.expiresAt)
  } catch {
    // 重签失败不动本次保存结果（后端已落库，姓名下次登录即刷新）
  }
}

const save = async () => {
  const elForm = await getElFormExpose()
  const valid = await elForm?.validate().catch(() => false)
  if (!valid) return

  try {
    await ElMessageBox.confirm('确定保存这三项资料吗？', '提示', {
      confirmButtonText: '确定',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }

  saveLoading.value = true
  try {
    // ⚠️ 必须带空串：邮箱/手机号留空 = 后端清空，取值时不能过滤掉空值
    const raw = await getFormData<Record<string, string>>(false)
    const result = await apsAuthApi.updateProfile({
      userName: String(raw.userName ?? '').trim(),
      email: String(raw.email ?? '').trim(),
      phoneNumber: String(raw.phoneNumber ?? '').trim()
    })
    await refreshTokenForNewName()
    ElMessage.success('资料已更新')
    emit('updated', result)
  } catch (error) {
    ElMessage.error(plainError(error, '保存失败，请稍后再试'))
  } finally {
    saveLoading.value = false
  }
}
</script>

<template>
  <Form :rules="rules" @register="formRegister" :schema="formSchema" />
  <ElDivider />
  <BaseButton type="primary" :loading="saveLoading" @click="save">保存</BaseButton>
</template>
