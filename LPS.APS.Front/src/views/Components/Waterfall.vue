<script setup lang="ts">
import { Waterfall } from '@/components/Waterfall'
import { ContentWrap } from '@/components/ContentWrap'
import { useI18n } from '@/hooks/web/useI18n'
import { ref, unref } from 'vue'
import { toAnyString } from '@/utils'

const data = ref<any>([])

/** 占位图：内联 SVG data URI。
 *  原先这里用 mockjs 的 Random.image()，它返回的是外部图床 http://dummyimage.com/…；
 *  线上包不能带 mock 库（且外部图床不可控），改为内联 SVG —— 无外部依赖。 */
const placeholder = (width: number, height: number, text: string) => {
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
    `<rect width="100%" height="100%" fill="#dcdfe6"/>` +
    `<text x="50%" y="50%" font-size="14" fill="#909399" text-anchor="middle" dominant-baseline="middle">${text}</text>` +
    `</svg>`
  return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg)
}

const getList = () => {
  const list: any = []
  for (let i = 0; i < 20; i++) {
    // 随机 100, 500 之间的整数
    const height = Math.floor(Math.random() * 400) + 100
    const width = Math.floor(Math.random() * 400) + 100
    list.push({
      width,
      height,
      id: toAnyString(),
      image_uri: placeholder(width, height, `${width}x${height}`)
    })
  }
  data.value = [...unref(data), ...list]
  if (unref(data).length >= 60) {
    end.value = true
  }
}
getList()

const { t } = useI18n()

const loading = ref(false)

const end = ref(false)

const loadMore = () => {
  loading.value = true
  setTimeout(() => {
    getList()
    loading.value = false
  }, 1000)
}
</script>

<template>
  <ContentWrap :title="t('router.waterfall')">
    <Waterfall
      :data="data"
      :loading="loading"
      :end="end"
      :props="{
        src: 'image_uri',
        height: 'height'
      }"
      @load-more="loadMore"
    />
  </ContentWrap>
</template>
