-- =====================================================================
-- 004 — Datos iniciales
--
-- Contenido migrado de studioData.ts en la app de referencia. Es idempotente:
-- se puede volver a ejecutar sin duplicar, gracias a los ON CONFLICT.
--
-- Las imágenes quedan en NULL a propósito. El frontend dibuja un
-- placeholder con gradiente cuando no hay URL, así que nunca se ve una
-- imagen rota. Cuando lleguen las fotos reales, se suben al Storage de
-- Supabase y se actualiza la columna.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Especialista
-- ---------------------------------------------------------------------
insert into public.specialists (id, name, title, bio, is_active)
values (
  1,
  'Andy',
  'Master Artist · Cosmetóloga & Estilista Titulada',
  'Especialista en alta cosmética y estilismo de gala. Certificación clínica y más de 320 servicios en Puebla.',
  true
)
on conflict (id) do update
  set name = excluded.name,
      title = excluded.title,
      bio = excluded.bio;

-- ---------------------------------------------------------------------
-- Horario: lunes a sábado, 9:00 a 20:00
-- ---------------------------------------------------------------------
insert into public.working_hours (specialist_id, weekday, start_time, end_time)
values
  (1, 1, '09:00', '20:00'),  -- lunes
  (1, 2, '09:00', '20:00'),  -- martes
  (1, 3, '09:00', '20:00'),  -- miércoles
  (1, 4, '09:00', '20:00'),  -- jueves
  (1, 5, '09:00', '20:00'),  -- viernes
  (1, 6, '09:00', '17:00')   -- sábado: cierra antes
on conflict (specialist_id, weekday) do update
  set start_time = excluded.start_time,
      end_time = excluded.end_time,
      is_active = true;

-- ---------------------------------------------------------------------
-- Servicios
-- ---------------------------------------------------------------------
insert into public.services (
  slug, name, tagline, description, badge,
  duration_minutes, price, rfc_required,
  includes, brands, preparation, sort_order
)
values
  (
    'maquillaje-profesional',
    'Maquillaje Profesional',
    'Social & Novias',
    'Técnica de piel blindada a prueba de agua y lágrimas, visagismo, pestañas 3D de visón personalizadas y ampolleta flash tensora. Ideal para bodas, XV años y galas.',
    'Piel Blindada',
    90, 1850, false,
    array[
      'Preparación dérmica profunda e hidratación biomimética',
      'Sellado hermético waterproof (resistencia 24h a sudor y lágrimas)',
      'Visagismo y contorno correctivo HD para foto y video',
      'Pestañas de efecto visón 3D a la medida',
      'Ampolleta flash efecto lifting y fijador dermocosmético',
      'Kit de retoque labial de cortesía'
    ],
    array['Dior Backstage', 'Huda Beauty', 'NARS Cosmetics', 'Charlotte Tilbury', 'Make Up For Ever'],
    'Acudir con el rostro limpio, sin crema pesada ni bloqueador grasoso. Evitar exfoliaciones agresivas 48 horas previas.',
    1
  ),
  (
    'peinado-de-gala',
    'Peinado de Gala & Novias',
    'Estilismo',
    'Ondas al agua estilo Hollywood, recogidos estructurados y peinados bohemios con protección térmica y sellado de brillo duradero sin rigidez.',
    'Fijación Flexible',
    60, 1200, false,
    array[
      'Diagnóstico capilar de textura y volumen',
      'Termoprotección francesa con efecto antifrizz',
      'Técnica de ondas pulidas Hollywood o recogido arquitectónico',
      'Colocación y ajuste seguro de tocado, velo o tiara',
      'Sellado con bruma de fijación flexible y brillo de seda'
    ],
    array['Kérastase Paris', 'Moroccanoil', 'Schwarzkopf Professional', 'GHD Styler'],
    'Cabello lavado 2 a 3 horas antes con shampoo neutro, 100% seco al momento de iniciar y sin aceites ni cremas para peinar.',
    2
  ),
  (
    'nail-arts-estructuras',
    'Nail Arts & Estructuras',
    'Manicura',
    'Manicura rusa estética con torno, diseño artístico a mano alzada, esmaltado semipermanente de larga duración, Soft Gel o acrílico fino con cristales Swarovski.',
    'Manicura Rusa',
    75, 850, false,
    array[
      'Limpieza profunda y no invasiva de cutícula combinada con fresas diamantadas',
      'Nivelación y fortalecimiento de uña natural con Rubber Base o Soft Gel',
      'Esmaltado semipermanente de alta pigmentación bajo cutícula (efecto cremallera)',
      'Diseño minimalista de autor a mano alzada y cristales Swarovski genuinos',
      'Hidratación de cutícula con aceite de jojoba y masaje con crema aromaterapéutica'
    ],
    array['OPI GelColor', 'Gelish Harmony', 'Apres Gel-X', 'Swarovski Elements', 'Cuccio Pro'],
    'Si requieres retiro de producto acrílico previo, agrégalo en los complementos para garantizar el tiempo de sesión adecuado.',
    3
  ),
  (
    'extensiones-cabello',
    'Extensiones de Cabello Premium',
    '100% Humano',
    'Aplicación profesional de cabello 100% virgen con técnica invisible (nanoring, microrings o punto invisible). Cero tracción, máxima ligereza y adaptación tonal.',
    'Punto Invisible',
    120, 3500, false,
    array[
      'Diagnóstico de densidad y matizado personalizado tono a tono',
      'Hebras 100% cabello humano virgen calidad Remy con cutícula intacta',
      'Montura milimétrica invisible libre de calor y químicos agresivos',
      'Corte de integración y pulido para caída 100% natural',
      'Instructivo de mantenimiento y cepillo especial de cerdas naturales'
    ],
    array['Remy Hair Select', 'Great Lengths Bio', 'Olaplex No. 7'],
    'Cabello limpio y desenredado. Se sugiere valoración previa para determinar el gramaje y largo ideal (18", 22" o 26").',
    4
  ),
  (
    'cursos-personalizados',
    'Cursos Personalizados 1 a 1',
    'Certificación',
    'Masterclasses individuales de Automaquillaje, Perfeccionamiento de Técnicas para profesionales y Formación en Nail Art. Incluye manual teórico y diploma oficial.',
    'Academia VIP',
    180, 2800, true,
    array[
      'Capacitación 100% privada con Andy sin grupos distractores',
      'Uso ilimitado de insumos de cabina de alta gama durante la práctica',
      'Dossier impreso a color con morfología, colorimetría y listas de productos recomendados',
      'Diploma con validez curricular expedido por Andy Cosmetología Puebla Studio',
      '30 días de asesoría y resolución de dudas directa por WhatsApp'
    ],
    array['Dior', 'Huda Beauty', 'NARS', 'Anastasia Beverly Hills', 'OPI'],
    'Para automaquillaje, puedes traer tu cosmetiquera actual para evaluar tus productos. Todos los materiales de cabina están incluidos.',
    5
  )
on conflict (slug) do update
  set name = excluded.name,
      tagline = excluded.tagline,
      description = excluded.description,
      badge = excluded.badge,
      duration_minutes = excluded.duration_minutes,
      price = excluded.price,
      includes = excluded.includes,
      brands = excluded.brands,
      preparation = excluded.preparation,
      sort_order = excluded.sort_order;

-- ---------------------------------------------------------------------
-- Complementos
--
-- El precio total de una cita es el servicio más los complementos, así que
-- cada uno guarda a qué servicio pertenece.
-- ---------------------------------------------------------------------
insert into public.addons (service_id, name, description, price, extra_minutes)
select s.id, v.name, v.description, v.price, v.extra_minutes
from (values
  ('maquillaje-profesional', 'Ampolleta Flash Tensora Anti-Fatiga',
   'Concentrado dermocosmético tensor con péptidos y ácido hialurónico para un efecto glow radiante.', 250, 10),
  ('maquillaje-profesional', 'Diseño y Laminado Express de Ceja',
   'Perfilado con hilo orgánico y fijación semipermanente de textura pulida.', 200, 15),
  ('maquillaje-profesional', 'Tratamiento Labios de Seda & Exfoliación Colágeno',
   'Exfoliación con microgránulos botánicos y mascarilla selladora de colágeno.', 180, 10),
  ('peinado-de-gala', 'Spray Termoprotector Frances',
   'Protección térmica de grado salón que mantiene el brillo sin sensación acartonada.', 150, 5),
  ('peinado-de-gala', 'Perlas y Accesorios de Elección',
   'Selección de accesorios de tocado, velo o tiara según el estilo de la sesión.', 220, 10),
  ('nail-arts-estructuras', 'Retiro Suave de Gel o Acrílico Previo',
   'Eliminación segura sin limado excesivo para preservar la salud de la uña.', 150, 20),
  ('nail-arts-estructuras', 'Diseño de Autor con Cristales',
   'Diseño artwork a mano alzada con cristales Swarovski genuinos.', 120, 15)
) as v(service_slug, name, description, price, extra_minutes)
join public.services s on s.slug = v.service_slug
where not exists (
  select 1 from public.addons a
  where a.service_id = s.id and a.name = v.name
);

-- ---------------------------------------------------------------------
-- Cursos
-- ---------------------------------------------------------------------
insert into public.courses (
  slug, title, tagline, description, tag, level,
  duration_label, session_count, price, deposit_amount,
  topics, includes
)
values
  (
    'automaquillaje-vip-pro',
    'Automaquillaje VIP Pro',
    'Transformación total de tu rutina de belleza',
    'Descubre los rasgos únicos de tu rostro y domina una rutina de día fresca y una de noche glamurosa que dure intacta horas.',
    'Nivel Inicial / Intermedio',
    'Principiantes y Entusiastas',
    '4 horas en 1 sesión intensiva',
    1, 2800, 840,
    array[
      'Visagismo y tipo de óvalo facial',
      'Preparación de piel según tu biotipo dérmico (grasa, seca, mixta o sensible)',
      'Selección exacta del subtono y base de maquillaje',
      'Técnicas de delineado adaptativo para tu tipo de ojo',
      'Diseño y relleno natural de cejas',
      'Transición de maquillaje de día a look nocturno con destellos'
    ],
    array[
      'Dossier impreso con guía de brochas y lista de marcas recomendadas',
      'Uso de todo el arsenal de maquillaje del estudio durante la clase',
      'Depuración personalizada de tu cosmetiquera',
      'Diploma de participación avalado por Andy Cosmetología'
    ]
  ),
  (
    'piel-blindada-novias',
    'Perfeccionamiento: Piel Blindada & Novias',
    'La técnica estrella más cotizada en Puebla',
    'Aprende a crear pieles de efecto aerógrafo, 100% impermeables al agua, lágrimas y clima húmedo.',
    'Especialización Profesional',
    'Maquillistas en activo y cosmetólogas',
    '6 horas de inmersión práctica',
    1, 3600, 1080,
    array[
      'Química cosmética: incompatibilidades entre primers y bases de silicona/agua',
      'Técnica secreta de emulsión y fijación hermética sin efecto acartonado',
      'Visagismo HD y corrección de hiperpigmentación severa',
      'Pestañas personalizadas capa por capa efecto visón 3D',
      'Técnicas de labios con relieve degradado a prueba de transferencias',
      'Estrategias de pricing y contratos de servicio para bodas'
    ],
    array[
      'Modelo real para práctica supervisada en cabina',
      'Guía paso a paso de productos y diluyentes exactos',
      'Plantilla de contrato legal para servicios de novias',
      'Diploma con valor curricular de Especialista en Piel Blindada'
    ]
  ),
  (
    'manicura-rusa-soft-gel',
    'Manicura Rusa Estética & Soft Gel Avanzado',
    'El estándar europeo de máxima pulcritud',
    'Manejo quirúrgico del torno para una cutícula impecable y nivelación con Rubber Base y Soft Gel.',
    'Estructura & Anatomía',
    'Manicuristas y principiantes dedicadas',
    '8 horas (divididas en 2 sesiones)',
    2, 3900, 1170,
    array[
      'Anatomía del aparato ungueal y prevención de onicólisis',
      'Clasificación y uso seguro de fresas diamantadas y de carburo',
      'Técnica rusa combinada (tijera y fresa llama)',
      'Nivelación del ápice para corregir uñas convexas o cóncavas',
      'Esmaltado profundo bajo bolsillo de cutícula para semanas de crecimiento limpio',
      'Fotografía de portafolio editorial con luz anular y macro'
    ],
    array[
      'Kit teórico de bioseguridad y esterilización de instrumental',
      'Práctica en modelo bajo supervisión directa de Andy',
      'Acceso a marcas de geles profesionales europeas',
      'Certificación oficial en Manicura Rusa Estética'
    ]
  )
on conflict (slug) do update
  set title = excluded.title,
      tagline = excluded.tagline,
      description = excluded.description,
      tag = excluded.tag,
      level = excluded.level,
      duration_label = excluded.duration_label,
      session_count = excluded.session_count,
      price = excluded.price,
      deposit_amount = excluded.deposit_amount,
      topics = excluded.topics,
      includes = excluded.includes;

-- ---------------------------------------------------------------------
-- Productos
--
-- Catálogo semilla de la tienda. Los precios son los de los insumos que
-- Andy usa en cabina.
-- ---------------------------------------------------------------------
insert into public.products (slug, name, brand, category, description, icon, price)
values
  ('olaplex-no-3', 'Olaplex No. 3 Reparador', 'Olaplex', 'Cuidado Capilar',
   'Reconstructor de enlaces para cabello dañado por calor y química.', 'spa', 650),
  ('olaplex-no-7', 'Olaplex No. 7 Aceite Protector', 'Olaplex', 'Cuidado Capilar',
   'Aceite seco que sella el corte y reduce el frizz hasta 72 horas.', 'water_drop', 720),
  ('kerastase-bain-satin', 'Kérastase Nutritive Bain Satin', 'Kérastase', 'Cuidado Capilar',
   'Baño nutritivo para cabello grueso, seco o rizado.', 'favorite', 890),
  ('moroccanoil-treatment', 'Moroccanoil Treatment', 'Moroccanoil', 'Cuidado Capilar',
   'Tratamiento con aceite de argán para brillo y suavidad.', 'oil_barrel', 950),
  ('gel-fijacion-firme', 'Gel de Fijación Firme', 'Studio Exclusivo', 'Estilizado',
   'Fijación fuerte sin residuo ni sensación acartonada.', 'blur_on', 320),
  ('spray-antifrizz', 'Spray Antifrizz', 'Studio Exclusivo', 'Estilizado',
   'Termoprotección y control de frizz para peinados de gala.', 'air', 380),
  ('gel-cuticula-premium', 'Gel de Cutícula Premium', 'Studio Exclusivo', 'Manicura',
   'Gel constructor para nivelación y manicura rusa.', 'back_hand', 290),
  ('aceite-jojoba-cuticula', 'Aceite de Jojoba para Cutícula', 'Studio Exclusivo', 'Manicura',
   'Hidratación profunda de cutícula y masaje aromaterapéutico.', 'eco', 260)
on conflict (slug) do update
  set name = excluded.name,
      brand = excluded.brand,
      category = excluded.category,
      description = excluded.description,
      icon = excluded.icon,
      price = excluded.price;

-- Stock inicial de la presentación de 250/100 ml.
insert into public.product_variants (product_id, size_label, stock_on_hand)
select p.id, v.size_label, v.stock
from (values
  ('olaplex-no-3',          '100 ml', 12),
  ('olaplex-no-7',          '100 ml',  8),
  ('kerastase-bain-satin',  '250 ml',  6),
  ('moroccanoil-treatment', '250 ml',  4),
  ('gel-fijacion-firme',    '150 ml', 15),
  ('spray-antifrizz',       '200 ml',  9),
  ('gel-cuticula-premium',  '30 ml', 11),
  ('aceite-jojoba-cuticula','30 ml',  3)
) as v(product_slug, size_label, stock)
join public.products p on p.slug = v.product_slug
where not exists (
  select 1 from public.product_variants pv
  where pv.product_id = p.id and pv.size_label = v.size_label
);

-- ---------------------------------------------------------------------
-- Reseñas
-- ---------------------------------------------------------------------
insert into public.reviews (author_name, role_label, initials, rating, body, sort_order)
values
  ('Valeria R.', 'Novia VIP • Hacienda San José', 'VR', 5,
   'Mi maquillaje de boda duró intacto desde las 11:00 AM hasta las 4:00 AM del día siguiente. Lloré en la misa, bailé toda la noche y la piel se veía tersa, luminosa y sin brillo graso. Andy es una maga total.', 1),
  ('Camila Morales', 'Maquillista Profesional • Alumna Certificada', 'CM', 5,
   'Tomé el curso 1 a 1 de perfeccionamiento en piel blindada. Andy te comparte todos sus secretos sin reservas: productos exactos, ángulos de brocha y mezclas. Ya aumenté los precios de mis servicios gracias a su técnica.', 2),
  ('Sofía Salgado', 'Clienta Frecuente • Zona La Paz', 'SS', 5,
   'La manicura rusa con Andy es una adicción. La precisión de la cutícula es quirúrgica y mis uñas en Soft Gel resisten 4 semanas intactas. La privacidad y la paz del estudio en Puebla no tienen comparación.', 3),
  ('Mariana Elizalde', 'Graduada Tec de Monterrey', 'ME', 5,
   'Me peinó y maquilló para mi fiesta de graduación. Las ondas Hollywood aguantaron hasta el amanecer sin sentirse tiesas y la atención personalizada en el atelier te hace sentir como en un spa privado.', 4)
on conflict do nothing;

-- ---------------------------------------------------------------------
-- FAQ
-- ---------------------------------------------------------------------
insert into public.faqs (question, answer, category, sort_order)
values
  ('¿Cómo aseguro mi fecha y cuáles son las políticas de anticipo?',
   'Las citas se confirman con un anticipo del 30% vía transferencia bancaria o tarjeta. El saldo restante se liquida el día del servicio en cabina (efectivo, tarjeta o transferencia). En caso de requerir cambio de horario o día, puedes reprogramar con al menos 24 horas de antelación sin penalización.',
   'Reservas', 1),
  ('¿Cómo debo preparar mi rostro y cabello antes de llegar a la cita?',
   'Para maquillaje: acude con la piel limpia, sin residuos de máscara de pestañas ni protector solar pesado (nosotros realizamos la preparación e hidratación clínica adecuada). Para peinado: cabello lavado 2 a 3 horas antes y completamente seco, sin aceites ni cremas para peinar.',
   'Preparación', 2),
  ('¿Los cursos incluyen materiales o debo llevar mi propio kit?',
   'Todos los materiales, pincelería y cosmecéuticos para las prácticas en el estudio están 100% incluidos. Se te proporciona un manual de estudio impreso a color y diploma de certificación. Te invitamos a traer tu cosmetiquera personal para depurar y asesorarte sobre los productos que ya posees.',
   'Cursos', 3),
  ('¿Atienden eventos a domicilio en Puebla y zonas aledañas?',
   'Sí. Brindamos servicio a domicilio y cobertura para paquetes nupciales en Puebla capital, Cholula, Atlixco y alrededores con viáticos correspondientes calculados de forma transparente según la distancia. Consulta disponibilidad por WhatsApp Concierge.',
   'Ubicación', 4),
  ('¿Cuánto tiempo dura la técnica de Piel Blindada?',
   'Nuestra técnica de Piel Blindada exclusiva resiste de 16 a 24 horas intacta frente a lágrimas, transpiración, abrazos y condiciones climáticas extremas sin quebrarse ni generar brillo excesivo en zona T.',
   'Servicios', 5)
on conflict do nothing;