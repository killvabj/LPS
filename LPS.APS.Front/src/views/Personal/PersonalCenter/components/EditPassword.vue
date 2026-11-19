<script setup lang="ts">
/**
 * APS V1 4号位 — 个人中心「修改密码」
 *
 * 2026-11-18：接后端真实端点 `POST /api/auth/change-password`
 *   —— 依据 3号位《个人中心改资料与改密码端点落码_回执 v1.0_20261117》R2 / Q1。
 *   - 后端规则（`AuthService.ChangePasswordAsync`）：
 *       · 旧密码必须正确（错 ⇒ 400「旧密码不正确」）；
 *       · 新密码 **8~128 位**（错 ⇒ 400「新密码长度须为 8~128 位」）；
 *       · 新密码 ≠ 旧密码、≠ 登录名（`UserCode`）。
 *   - Q1：后端改密成功后 **清空 RefreshToken**（旧会话无法续期），
 *     但已签发的 AccessToken 在剩余有效期内仍有效 ⇒ 前端改密成功后
 *     **清本地 token 并跳登录页**，实现「立即强制重登」。
 *
 * ⚠️ 旧实现是「假成功」（改完只弹一句「修改成功」，密码根本没改）——本轮整块换成真实调用。
 */

import { Form, FormSchema } from '@/components/Form'
import { useForm } from '@/hooks/web/useForm'
import { reactive, ref } from 'vue'
import { useValidator } from '@/hooks/web/useValidator'
import { ElMessage, ElMessageBox, ElDivider } from 'element-plus'
import { apsAuthApi } from '@/api/aps-v1'
import { useUserStore } from '@/store/modules/user'
import { useApsAuthStore } from '@/store/modules/aps/auth'

const { required } = useValidator()

const { formRegister, formMethods } = useForm()
const { getFormData, getElFormExpose } = formMethods

const userStore = useUserStore()
const apsAuth = useApsAuthStore()

const formSchema = reactive<FormSchema[]>([
  {
    field: 'password',
    label: '旧密码',
    component: 'InputPassword',
    colProps: {
      span: 24
    }
  },
  {
    field: 'newPassword',
    label: '新密码',
    component: 'InputPassword',
    colProps: {
      span: 24
    },
    componentProps: {
      strength: true
    }
  },
  {
    field: 'newPassword2',
    label: '确认新密码',
    component: 'InputPassword',
    colProps: {
      span: 24
    },
    componentProps: {
      strength: true
    }
  }
])

/** 登录名（= 用户编码）：后端禁止把新密码设成登录名 */
const loginCode = (): string => apsAuth.userInfo?.userCode ?? ''

const rules = reactive({
  password: [required()],
  newPassword: [
    required(),
    {
      asyncValidator: async (_, val, callback) => {
        const v = String(val ?? '')
        if (v.length < 8 || v.length > 128) {
          callback(new Error('新密码长度须为 8~128 位'))
          return
        }
        const formData = await getFormData<Record<string, string>>(false)
        if (v === String(formData.password ?? '')) {
          callback(new Error('新密码不能与旧密码相同'))
          return
        }
        const code = loginCode()
        if (code && v === code) {
          callback(new Error('新密码不能与登录名相同'))
          return
        }
        if (v !== String(formData.newPassword2 ?? '')) {
          callback(new Error('新密码与确认新密码不一致'))
          return
        }
        callback()
      }
    }
  ],
  newPassword2: [
    required(),
    {
      asyncValidator: async (_, val, callback) => {
        const formData = await getFormData<Record<string, string>>(false)
        if (String(val ?? '') !== String(formData.newPassword ?? '')) {
          callback(new Error('确认新密码与新密码不一致'))
          return
        }
        callback()
      }
    }
  ]
})

const saveLoading = ref(false)

/** 只把中文错误原文摆到页面上（后端 message 已是大白话；axios 英文原文不外泄） */
function plainError(error: unknown, fallback: string): string {
  const msg = (error as Error)?.message ?? ''
  return /[一-龥]/.test(msg) ? msg : fallback
}

const save = async () => {
  const elForm = await getElFormExpose()
  const valid = await elForm?.validate().catch(() => false)
  if (!valid) return

  try {
    await ElMessageBox.confirm('确定要修改登录密码吗？', '提示', {
      confirmButtonText: '确定',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }

  saveLoading.value = true
  try {
    const raw = await getFormData<Record<string, string>>(false)
    await apsAuthApi.changePassword({
      oldPassword: String(raw.password ?? ''),
      newPassword: String(raw.newPassword ?? '')
    })
    ElMessage.success('密码已修改，请重新登录')
    // Q1 强制重登：后端已清 RefreshToken ⇒ 本地把 token 清干净并跳登录页
    apsAuth.reset()
    userStore.reset()
  } catch (error) {
    ElMessage.error(plainError(error, '修改失败，请稍后再试'))
  } finally {
    saveLoading.value = false
  }
}
</script>

<template>
  <Form :rules="rules" @register="formRegister" :schema="formSchema" />
  <ElDivider />
  <BaseButton type="primary" :loading="saveLoading" @click="save">确认修改</BaseButton>
</template>
