<script setup>
import { useId } from 'vue'
defineProps({ title: { type: String, required: true } })
const id = useId()
</script>

<!-- A card drawn as a Haiku window: yellow tab only as wide as its title, sitting on the frame. -->
<template>
  <section class="win" :aria-labelledby="id">
    <div class="win-tab">
      <span class="win-close" aria-hidden="true"></span>
      <h2 :id="id">{{ title }}</h2>
    </div>
    <div class="win-body"><slot /></div>
  </section>
</template>
